using Jint;
using Jint.Native;
using Sideload.Host;
using Sideload.Script;

namespace Sideload.Devtools.Cdp
{
    /// <summary>
    /// The Runtime domain: everything the console does.
    ///
    /// An expression typed into DevTools ends up in the page's own Jint engine, on the frame it was typed on, with
    /// the page's `document`, `s1` and every global its script defined. There is no second engine and no sandbox -
    /// the whole point is to reach the live page.
    /// </summary>
    internal static class RuntimeDomain
    {
        /// <summary>A page has exactly one execution context, so its id is a constant.</summary>
        internal const int ContextId = 1;

        internal static string Enable(CdpSession session)
        {
            session.RuntimeEnabled = true;

            // Order matters: the reply, then the context, then whatever the page already logged. A console message
            // that arrives before the context it belongs to is dropped by the frontend.
            session.EmitAfterReply("Runtime.executionContextCreated", ContextJson(session));
            LogDomain.Replay(session);

            return Json.EmptyObject;
        }

        internal static string Evaluate(CdpSession session, JsonValue args)
        {
            string expression = args["expression"].AsString();
            if (string.IsNullOrEmpty(expression)) throw new CdpException(CdpException.InvalidParams, "expression is empty");

            string group = args["objectGroup"].AsString("console");
            bool byValue = args["returnByValue"].AsBool();
            bool preview = args["generatePreview"].AsBool(true);
            bool awaitPromise = args["awaitPromise"].AsBool();

            Engine engine = ScriptOf(session).Engine;

            try
            {
                JsValue value = engine.Evaluate(expression, "<devtools>");

                if (awaitPromise)
                {
                    // A promise only settles when the engine drains its job queue, which normally happens on the
                    // page's next tick. The console asked for the settled value, so drain it now.
                    engine.Advanced.ProcessTasks();
                    value = value.UnwrapIfPromise();
                }

                return new Json.Obj()
                    .Raw("result", Remote.Describe(value, session.Objects, group, byValue, preview))
                    .Done();
            }
            catch (Exception e)
            {
                return Threw(session, e, group);
            }
        }

        internal static string CallFunctionOn(CdpSession session, JsonValue args)
        {
            string declaration = args["functionDeclaration"].AsString();
            if (string.IsNullOrEmpty(declaration))
                throw new CdpException(CdpException.InvalidParams, "functionDeclaration is empty");

            string group = args["objectGroup"].AsString("console");
            bool byValue = args["returnByValue"].AsBool();
            bool preview = args["generatePreview"].AsBool(true);
            string objectId = args["objectId"].AsString();

            Engine engine = ScriptOf(session).Engine;

            JsValue self = JsValue.Undefined;
            if (!string.IsNullOrEmpty(objectId) && !session.Objects.TryGet(objectId, out self))
                throw new CdpException(CdpException.InvalidParams, "no object with id " + objectId);

            try
            {
                // Wrapped in parentheses because the frontend sends a bare `function (a) { ... }`, which is a
                // declaration in statement position and an expression here.
                JsValue function = engine.Evaluate("(" + declaration + ")", "<devtools>");

                var call = new List<JsValue>();
                JsonValue list = args["arguments"];
                for (int i = 0; i < list.Count; i++)
                {
                    JsonValue argument = list[i];
                    string byId = argument["objectId"].AsString();

                    if (!string.IsNullOrEmpty(byId))
                    {
                        call.Add(session.Objects.TryGet(byId, out JsValue held) ? held : JsValue.Undefined);
                        continue;
                    }

                    call.Add(argument["value"].IsMissing ? JsValue.Undefined : Remote.FromJson(engine, argument["value"]));
                }

                JsValue result = engine.Invoke(function, self, call.ToArray());
                return new Json.Obj()
                    .Raw("result", Remote.Describe(result, session.Objects, group, byValue, preview))
                    .Done();
            }
            catch (Exception e)
            {
                return Threw(session, e, group);
            }
        }

        internal static string GetProperties(CdpSession session, JsonValue args)
        {
            string objectId = args["objectId"].AsString();
            if (!session.Objects.TryGet(objectId, out JsValue value))
                throw new CdpException(CdpException.InvalidParams, "no object with id " + objectId);

            // Accessors are read, not listed separately: this host has no way to show a getter's source, and a
            // console row that says "(...)" forever is worse than the value it would have returned.
            if (args["accessorPropertiesOnly"].AsBool()) return new Json.Obj().Raw("result", "[]").Done();

            return new Json.Obj()
                .Raw("result", Remote.Properties(value, session.Objects, args["objectGroup"].AsString("console")))
                .Done();
        }

        internal static string ReleaseObject(CdpSession session, JsonValue args)
        {
            session.Objects.Release(args["objectId"].AsString());
            return Json.EmptyObject;
        }

        internal static string ReleaseObjectGroup(CdpSession session, JsonValue args)
        {
            session.Objects.ReleaseGroup(args["objectGroup"].AsString());
            return Json.EmptyObject;
        }

        /// <summary>What the console offers as completions for a bare name. The page's globals live on the global
        /// object rather than in a lexical scope, so there is nothing to add here.</summary>
        internal static string GlobalLexicalScopeNames() => new Json.Obj().Raw("names", "[]").Done();

        internal static string ContextJson(CdpSession session)
        {
            WebView view = Targets.Find(session.TargetId);

            string context = new Json.Obj()
                .Num("id", ContextId)
                .Str("uniqueId", session.TargetId + "." + ContextId)
                .Str("origin", Targets.OriginOf(view))
                .Str("name", view?.AppId ?? session.TargetId)
                .Raw("auxData", new Json.Obj()
                    .Bool("isDefault", true)
                    .Str("type", "default")
                    .Str("frameId", Targets.FrameOf(session.TargetId))
                    .Done())
                .Done();

            return new Json.Obj().Raw("context", context).Done();
        }

        /// <summary>The page this session is attached to, or a protocol error explaining why there is none.</summary>
        internal static ScriptHost ScriptOf(CdpSession session)
        {
            WebView view = Targets.Find(session.TargetId)
                ?? throw new CdpException(CdpException.ServerError,
                    $"the page '{session.TargetId}' is no longer mounted");

            return view.Script
                ?? throw new CdpException(CdpException.ServerError,
                    "the page has not been built yet - open the app on the phone first");
        }

        /// <summary>An evaluation that threw. The thrown value is returned as the result as well, because that is
        /// what the console renders in the red row.</summary>
        private static string Threw(CdpSession session, Exception error, string group)
        {
            string details = Remote.ExceptionDetails(error, session.Objects, group);

            return new Json.Obj()
                .Raw("result", new Json.Obj().Str("type", "object").Str("subtype", "error")
                    .Str("className", error.GetType().Name).Str("description", error.Message).Done())
                .Raw("exceptionDetails", details)
                .Done();
        }
    }
}
