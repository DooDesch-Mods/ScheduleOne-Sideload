namespace Sideload.Devtools.Cdp
{
    /// <summary>
    /// Which protocol methods exist, and what runs them.
    ///
    /// Five domains are real: Runtime (evaluate, inspect values), Log (the page's console), Page (enough for DevTools
    /// to attach to something it recognises as a page), DOM (the Elements tree, and editing it) and CSS (the Styles
    /// and Computed panes). Everything else is answered with "not implemented" on purpose - claiming a domain and
    /// then returning nothing is what makes an embedded inspector confusing to use.
    ///
    /// Every method here runs on Unity's main thread; <see cref="CdpSession"/> puts it there.
    /// </summary>
    internal static class Domains
    {
        /// <summary>
        /// Methods DevTools sends the moment it attaches, that only turn a subscription on or configure something
        /// this host has no equivalent for. Answered with an empty result so the frontend's attach sequence completes
        /// cleanly instead of filling its own console with failures.
        ///
        /// This list is what a current frontend actually sends, read off the wire rather than guessed. Methods that
        /// would have to RETURN something (Storage.getStorageKey), or that start a feature this host cannot deliver
        /// (Page.startScreencast, Runtime.addBinding, CSS.takeComputedStyleUpdates), are deliberately not in it: an
        /// empty answer to those is a lie the frontend then waits on.
        /// </summary>
        private static readonly HashSet<string> Accepted = new HashSet<string>(StringComparer.Ordinal)
        {
            "Autofill.setAddresses",
            "Debugger.setAsyncCallStackDepth",
            "Debugger.setBlackboxPatterns",
            "Debugger.setBlackboxExecutionContexts",
            "Debugger.setPauseOnExceptions",
            "DOM.hideHighlight",
            "DOM.markUndoableState",
            "DOM.setInspectedNode",
            "DOMDebugger.setBreakOnCSPViolation",
            "Emulation.setAutoDarkModeOverride",
            "Emulation.setEmulatedMedia",
            "Emulation.setEmulatedVisionDeficiency",
            "Emulation.setFocusEmulationEnabled",
            "Log.clear",
            "Log.startViolationsReport",
            "Log.stopViolationsReport",
            "Network.clearAcceptedEncodingsOverride",
            "Network.setAttachDebugStack",
            "Network.setBlockedURLs",
            "Network.setCacheDisabled",
            "Overlay.hideHighlight",
            "Overlay.highlightNode",
            "Overlay.setInspectMode",
            "Overlay.setShowContainerQueryOverlays",
            "Overlay.setShowFlexOverlays",
            "Overlay.setShowGridOverlays",
            "Overlay.setShowIsolatedElements",
            "Overlay.setShowScrollSnapOverlays",
            "Overlay.setShowViewportSizeOnResize",
            "Page.bringToFront",
            "Page.setAdBlockingEnabled",
            "Page.setBypassCSP",
            "Page.setInterceptFileChooserDialog",
            "Page.setLifecycleEventsEnabled",
            "Profiler.setSamplingInterval",
            "Runtime.discardConsoleEntries",
            "Runtime.runIfWaitingForDebugger",
            "Runtime.setAsyncCallStackDepth",
            "Runtime.setCustomObjectFormatterEnabled",
            "Runtime.setMaxCallStackSizeToCapture",
            "Target.setAutoAttach",
            "Target.setDiscoverTargets",
            "Target.setRemoteLocations",
        };

        internal static string Invoke(CdpSession session, string method, JsonValue args)
        {
            switch (method)
            {
                // ----- Runtime -----
                case "Runtime.enable": return RuntimeDomain.Enable(session);
                case "Runtime.evaluate": return RuntimeDomain.Evaluate(session, args);
                case "Runtime.callFunctionOn": return RuntimeDomain.CallFunctionOn(session, args);
                case "Runtime.getProperties": return RuntimeDomain.GetProperties(session, args);
                case "Runtime.releaseObject": return RuntimeDomain.ReleaseObject(session, args);
                case "Runtime.releaseObjectGroup": return RuntimeDomain.ReleaseObjectGroup(session, args);
                case "Runtime.globalLexicalScopeNames": return RuntimeDomain.GlobalLexicalScopeNames();
                case "Runtime.disable": session.RuntimeEnabled = false; return Json.EmptyObject;

                // ----- Log -----
                case "Log.enable": session.LogEnabled = true; return Json.EmptyObject;
                case "Log.disable": session.LogEnabled = false; return Json.EmptyObject;

                // ----- Page -----
                case "Page.enable": return PageDomain.Enable(session);
                case "Page.disable": session.PageEnabled = false; return Json.EmptyObject;
                case "Page.getResourceTree": return PageDomain.GetResourceTree(session);
                case "Page.getFrameTree": return PageDomain.GetFrameTree(session);
                case "Page.getNavigationHistory": return PageDomain.GetNavigationHistory(session);
                case "Page.getLayoutMetrics": return PageDomain.GetLayoutMetrics(session);
                case "Page.reload": return PageDomain.Reload(session);

                // ----- CSS -----
                case "CSS.enable": return CssDomain.Enable(session);
                case "CSS.disable": session.CssEnabled = false; return Json.EmptyObject;
                case "CSS.getMatchedStylesForNode": return CssDomain.GetMatchedStylesForNode(session, args);
                case "CSS.getInlineStylesForNode": return CssDomain.GetInlineStylesForNode(session, args);
                case "CSS.getComputedStyleForNode": return CssDomain.GetComputedStyleForNode(session, args);
                case "CSS.forcePseudoState": return CssDomain.ForcePseudoState(session, args);
                case "CSS.getStyleSheetText": return CssDomain.GetStyleSheetText(session, args);
                case "CSS.setStyleTexts": return CssDomain.SetStyleTexts(session, args);

                // ----- DOM -----
                case "DOM.enable": session.DomEnabled = true; return Json.EmptyObject;
                case "DOM.disable": session.DomEnabled = false; return Json.EmptyObject;
                case "DOM.getDocument": return DomDomain.GetDocument(session, args);
                case "DOM.requestChildNodes": return DomDomain.RequestChildNodes(session, args);
                case "DOM.getOuterHTML": return DomDomain.GetOuterHtml(session, args);
                case "DOM.setOuterHTML": return DomDomain.SetOuterHtml(session, args);
                case "DOM.setAttributeValue": return DomDomain.SetAttributeValue(session, args);
                case "DOM.setAttributesAsText": return DomDomain.SetAttributesAsText(session, args);
                case "DOM.removeNode": return DomDomain.RemoveNode(session, args);
                case "DOM.setNodeValue": return DomDomain.SetNodeValue(session, args);
                case "DOM.describeNode": return DomDomain.DescribeNode(session, args);
                case "DOM.resolveNode": return DomDomain.ResolveNode(session, args);

                default:
                    if (Accepted.Contains(method)) return Json.EmptyObject;

                    // A bare enable/disable is only a subscription toggle; answering it keeps the frontend's attach
                    // sequence quiet without pretending the domain behind it does anything.
                    if (method.EndsWith(".enable", StringComparison.Ordinal) || method.EndsWith(".disable", StringComparison.Ordinal))
                        return Json.EmptyObject;

                    throw new CdpException(CdpException.MethodNotFound,
                        $"'{method}' is not implemented by Sideload (Runtime, Log, Page and DOM are).");
            }
        }
    }
}
