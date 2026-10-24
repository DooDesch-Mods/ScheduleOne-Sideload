// `s1api`, the part generated from S1API 3.2.1. Do not edit: a Sideload build rewrites this file from the S1API it
// is built against (tools/S1ApiGen). The hand-written part is s1api.d.ts, which references this file.
//
// Every static property, reading method and event of S1API is here under the class name in camelCase:
// S1API.GameTime.TimeManager.CurrentTime is `s1api.timeManager.currentTime`. Objects come two levels deep; a type
// ending in Brief is the second level and carries scalars only. Every value can be null: before a save has loaded,
// without S1API, or when the member is missing from the S1API the player has.

declare namespace S1ApiGen {
  /** S1API.Entities.Impostors.AvatarImpostorDefinition */
  interface AvatarImpostorDefinition {
    name: string | null;
    resourcePath: string | null;
  }

  /** S1API.Console.BaseConsoleCommand */
  interface BaseConsoleCommand {
    commandWord: string | null;
    commandDescription: string | null;
    exampleUsage: string | null;
  }

  /** S1API.Avatar.BasicAvatarSettings */
  interface BasicAvatarSettings {
    gender: number | null;
    weight: number | null;
    skinColor: { r: number; g: number; b: number; a: number } | null;
    hairStyle: string | null;
    hairColor: { r: number; g: number; b: number; a: number } | null;
    mouth: string | null;
    facialHair: string | null;
    facialDetails: string | null;
    facialDetailsIntensity: number | null;
    eyeballColor: { r: number; g: number; b: number; a: number } | null;
    upperEyeLidRestingPosition: number | null;
    lowerEyeLidRestingPosition: number | null;
    pupilDilation: number | null;
    eyebrowScale: number | null;
    eyebrowThickness: number | null;
    eyebrowRestingHeight: number | null;
    eyebrowRestingAngle: number | null;
    top: string | null;
    topColor: { r: number; g: number; b: number; a: number } | null;
    bottom: string | null;
    bottomColor: { r: number; g: number; b: number; a: number } | null;
    shoes: string | null;
    shoesColor: { r: number; g: number; b: number; a: number } | null;
    headwear: string | null;
    headwearColor: { r: number; g: number; b: number; a: number } | null;
    eyewear: string | null;
    eyewearColor: { r: number; g: number; b: number; a: number } | null;
  }

  /** S1API.Casino.BlackjackGame */
  interface BlackjackGame {
    stage: S1ApiGen.BlackjackStage | null;
    dealerScore: number | null;
    localPlayerScore: number | null;
    isLocalPlayerBlackjack: boolean | null;
    isLocalPlayerBust: boolean | null;
    isLocalPlayerInRound: boolean | null;
    readyPlayerCount: number | null;
    dealerHand: S1ApiGen.CasinoCardSnapshotBrief[] | null;
    name: string | null;
    position: { x: number; y: number; z: number } | null;
    isOpen: boolean | null;
    isWaitingForPlayers: boolean | null;
    localBet: number | null;
    betLimits: S1ApiGen.CasinoBetLimitsBrief | null;
    players: S1ApiGen.CasinoPlayerSnapshotBrief[] | null;
  }

  type BlackjackStage = 'WaitingForPlayers' | 'Dealing' | 'PlayerTurn' | 'DealerTurn' | 'Ending';

  /** S1API.Building.BuildEventArgs */
  interface BuildEventArgs {
    itemInstance: S1ApiGen.ItemInstanceBrief | null;
    itemId: string | null;
    storage: S1ApiGen.StorageEntityBrief | null;
  }

  /** S1API.Map.Building */
  interface Building {
    name: string | null;
  }

  /** S1API.Property.BusinessWrapper */
  interface BusinessWrapper {
    launderCapacity: number | null;
    launderingOperations: S1ApiGen.LaunderingOperationBrief[] | null;
    currentLaunderTotal: number | null;
    appliedLaunderLimit: number | null;
    launderingOperationCount: number | null;
    isAtLaunderingCapacity: boolean | null;
    launderingCapacityUsagePercent: number | null;
    propertyName: string | null;
    propertyCode: string | null;
    price: number | null;
    isOwned: boolean | null;
    employeeCapacity: number | null;
    exteriorSpawnPosition: { x: number; y: number; z: number } | null;
    interiorSpawnPosition: { x: number; y: number; z: number } | null;
    loadingDockCount: number | null;
    loadingDocks: S1ApiGen.LoadingDockBrief[] | null;
    defaultRotation: number | null;
    availableInDemo: boolean | null;
    isContentCulled: boolean | null;
    employeeCount: number | null;
    buildableItemCount: number | null;
    npcSpawnPosition: { x: number; y: number; z: number } | null;
    employeeIdlePointCount: number | null;
  }

  /** S1API.Cartel.Cartel */
  interface Cartel {
    status: S1ApiGen.CartelStatus | null;
    hoursSinceStatusChange: number | null;
    goonPool: S1ApiGen.GoonManagerBrief | null;
  }

  type CartelStatus = 'Unknown' | 'Truced' | 'Hostile' | 'Defeated';

  /** S1API.Casino.CasinoBetLimits, one level down: scalars only */
  interface CasinoBetLimitsBrief {
    minimum: number | null;
    maximum: number | null;
  }

  /** S1API.Casino.CasinoCardSnapshot, one level down: scalars only */
  interface CasinoCardSnapshotBrief {
    id: string | null;
    suit: S1ApiGen.CasinoCardSuit | null;
    value: S1ApiGen.CasinoCardValue | null;
    isFaceUp: boolean | null;
  }

  type CasinoCardSuit = 'Spades' | 'Hearts' | 'Diamonds' | 'Clubs';

  type CasinoCardValue = 'Blank' | 'Ace' | 'Two' | 'Three' | 'Four' | 'Five' | 'Six' | 'Seven' | 'Eight' | 'Nine' | 'Ten' | 'Jack' | 'Queen' | 'King';

  /** S1API.Casino.CasinoGameTable */
  interface CasinoGameTable {
    name: string | null;
    position: { x: number; y: number; z: number } | null;
    isOpen: boolean | null;
    isWaitingForPlayers: boolean | null;
    localBet: number | null;
    betLimits: S1ApiGen.CasinoBetLimitsBrief | null;
    players: S1ApiGen.CasinoPlayerSnapshotBrief[] | null;
  }

  /** S1API.Casino.CasinoPlayerSnapshot, one level down: scalars only */
  interface CasinoPlayerSnapshotBrief {
    name: string | null;
    seatIndex: number | null;
    score: number | null;
    isReady: boolean | null;
  }

  /** S1API.Law.CheckpointInfo */
  interface CheckpointInfo {
    location: S1ApiGen.CheckpointLocation | null;
    isEnabled: boolean | null;
    position: { x: number; y: number; z: number } | null;
    assignedOfficerCount: number | null;
    isGate1Open: boolean | null;
    isGate2Open: boolean | null;
    areBothGatesClosed: boolean | null;
    isAnyGateOpen: boolean | null;
    isOperational: boolean | null;
  }

  type CheckpointLocation = 'Western' | 'Docks' | 'NorthResidential' | 'WestResidential';

  /** S1API.Stations.ChemistryStationRecipe */
  interface ChemistryStationRecipe {
    recipeID: string | null;
    title: string | null;
    cookTimeMinutes: number | null;
    temperature: S1ApiGen.ChemistryStationRecipeTemperatureBrief | null;
    finalLiquidColor: { r: number; g: number; b: number; a: number } | null;
    product: S1ApiGen.ChemistryStationRecipeProductBrief | null;
    ingredients: S1ApiGen.ChemistryStationRecipeIngredientBrief[] | null;
    qualityCalculationMethod: S1ApiGen.QualityCalculationMethod | null;
    isDiscovered: boolean | null;
    isUnlocked: boolean | null;
  }

  /** S1API.Stations.ChemistryStationRecipeIngredient, one level down: scalars only */
  interface ChemistryStationRecipeIngredientBrief {
    itemIds: string[] | null;
    quantity: number | null;
  }

  /** S1API.Stations.ChemistryStationRecipeProduct, one level down: scalars only */
  interface ChemistryStationRecipeProductBrief {
    itemId: string | null;
    quantity: number | null;
  }

  /** S1API.Stations.ChemistryStationRecipeTemperature, one level down: scalars only */
  interface ChemistryStationRecipeTemperatureBrief {
    cookTemperature: number | null;
    cookTemperatureTolerance: number | null;
    cookTemperatureLowerBound: number | null;
    cookTemperatureUpperBound: number | null;
  }

  type ClothingClothingColor = 'White' | 'LightGrey' | 'DarkGrey' | 'Charcoal' | 'Black' | 'LightRed' | 'Red' | 'Crimson' | 'Orange' | 'Tan' | 'Brown' | 'Coral' | 'Beige' | 'Yellow' | 'Lime' | 'LightGreen' | 'DarkGreen' | 'Cyan' | 'SkyBlue' | 'Blue' | 'DeepBlue' | 'Navy' | 'DeepPurple' | 'Purple' | 'Magenta' | 'BrightPink' | 'HotPink';

  type ClothingClothingSlot = 'Feet' | 'Bottom' | 'Waist' | 'Top' | 'Outerwear' | 'Hands' | 'Neck' | 'Eyes' | 'Head' | 'Wrist';

  /** S1API.Items.Clothing.ClothingColorMetadata */
  interface ClothingColorMetadata {
    color: S1ApiGen.ClothingClothingColor | null;
    displayName: string | null;
    actualColor: { r: number; g: number; b: number; a: number } | null;
    labelColor: { r: number; g: number; b: number; a: number } | null;
  }

  /** S1API.Items.Clothing.ClothingSlotMetadata */
  interface ClothingSlotMetadata {
    slot: S1ApiGen.ClothingClothingSlot | null;
    displayName: string | null;
  }

  /** S1API.Products.CocaineDefinition */
  interface CocaineDefinition {
    price: number | null;
    basePrice: number | null;
    marketValue: number | null;
    properties: S1ApiGen.PropertyBaseBrief[] | null;
    drugTypeValues: S1ApiGen.DrugType[] | null;
    primaryDrugType: S1ApiGen.DrugType | null;
    basePurchasePrice: number | null;
    resellMultiplier: number | null;
    isUnlocked: boolean | null;
    requiresLevelToPurchase: boolean | null;
    requiredRank: S1ApiGen.FullRankBrief | null;
    hasStationItem: boolean | null;
    id: string | null;
    name: string | null;
    description: string | null;
    stackLimit: number | null;
    category: S1ApiGen.ItemCategory | null;
    availableInDemo: boolean | null;
    legalStatus: S1ApiGen.LegalStatus | null;
    guid: string | null;
  }

  type CustomProductMultiplayerPolicy = 'Reject';

  type CutsceneEndReason = 'Completed' | 'Skipped' | 'Stopped' | 'SceneChanged' | 'Failed';

  /** S1API.Cutscenes.CutsceneHandle */
  interface CutsceneHandle {
    id: string | null;
    name: string | null;
    isPlaying: boolean | null;
    elapsed: number | null;
    progress: number | null;
    endReason: S1ApiGen.CutsceneEndReason | null;
  }

  type Day = 'Monday' | 'Tuesday' | 'Wednesday' | 'Thursday' | 'Friday' | 'Saturday' | 'Sunday';

  /** S1API.DeadDrops.DeadDropInstance */
  interface DeadDropInstance {
    guid: string | null;
    name: string | null;
    description: string | null;
    region: S1ApiGen.Region | null;
    storage: S1ApiGen.StorageInstanceBrief | null;
    itemCount: number | null;
    isEmpty: boolean | null;
    position: { x: number; y: number; z: number } | null;
  }

  /** S1API.Deliveries.Delivery */
  interface Delivery {
    id: string | null;
    storeName: string | null;
    destinationCode: string | null;
    loadingDockIndex: number | null;
    loadingDock: S1ApiGen.LoadingDockBrief | null;
    destination: S1ApiGen.PropertyWrapperBrief | null;
    status: S1ApiGen.DeliveryStatus | null;
    minutesUntilArrival: number | null;
    items: S1ApiGen.DeliveryItemBrief[] | null;
    shop: S1ApiGen.ShopBrief | null;
    activeVehicle: S1ApiGen.LandVehicleBrief | null;
  }

  /** S1API.Deliveries.DeliveryItem, one level down: scalars only */
  interface DeliveryItemBrief {
    itemId: string | null;
    quantity: number | null;
  }

  /** S1API.Map.DeliveryLocation */
  interface DeliveryLocation {
    name: string | null;
    description: string | null;
    guid: string | null;
  }

  /** S1API.Deliveries.DeliveryReceipt */
  interface DeliveryReceipt {
    id: string | null;
    storeName: string | null;
    destinationCode: string | null;
    loadingDockIndex: number | null;
    items: S1ApiGen.DeliveryItemBrief[] | null;
  }

  type DeliveryStatus = 'InTransit' | 'Waiting' | 'Arrived' | 'Completed';

  type DrugType = 'Marijuana' | 'Methamphetamine' | 'Cocaine' | 'MDMA' | 'Shrooms' | 'Heroin';

  /** S1API.ExternalHosting.ExternalAppDiagnostic */
  interface ExternalAppDiagnostic {
    family: S1ApiGen.ExternalAppFamily | null;
    typeName: string | null;
    message: string | null;
  }

  type ExternalAppFamily = 'Phone' | 'TV';

  /** S1API.ExternalHosting.ExternalAppRegistration */
  interface ExternalAppRegistration {
    family: S1ApiGen.ExternalAppFamily | null;
    id: string | null;
    title: string | null;
    host: S1ApiGen.IExternalAppHostBrief | null;
  }

  /** S1API.Law.FootPatrolRoute */
  interface FootPatrolRoute {
    routeName: string | null;
    waypointCount: number | null;
    startWaypointIndex: number | null;
    position: { x: number; y: number; z: number } | null;
  }

  /** S1API.Leveling.FullRank */
  interface FullRank {
    rank: S1ApiGen.Rank | null;
    tier: number | null;
  }

  /** S1API.Leveling.FullRank, one level down: scalars only */
  interface FullRankBrief {
    rank: S1ApiGen.Rank | null;
    tier: number | null;
  }

  /** S1API.Cartel.GoonManager, one level down: scalars only */
  interface GoonManagerBrief {
    availableGoonCount: number | null;
  }

  /** S1API.ExternalHosting.IExternalAppHost, one level down: scalars only */
  interface IExternalAppHostBrief {
    allowExternalHosting: boolean | null;
  }

  type InputDeviceType = 'KeyboardMouse' | 'Gamepad';

  type ItemCategory = 'Product' | 'Packaging' | 'Growing' | 'Tools' | 'Furniture' | 'Lighting' | 'Cash' | 'Consumable' | 'Equipment' | 'Ingredient' | 'Decoration' | 'Clothing';

  /** S1API.Items.ItemDefinition */
  interface ItemDefinition {
    id: string | null;
    name: string | null;
    description: string | null;
    stackLimit: number | null;
    category: S1ApiGen.ItemCategory | null;
    availableInDemo: boolean | null;
    legalStatus: S1ApiGen.LegalStatus | null;
    guid: string | null;
  }

  /** S1API.Items.ItemInstance, one level down: scalars only */
  interface ItemInstanceBrief {
    quantity: number | null;
    isStackable: boolean | null;
  }

  /** S1API.Items.ItemSlotInstance, one level down: scalars only */
  interface ItemSlotInstanceBrief {
    quantity: number | null;
  }

  /** S1API.Audio.Jukebox */
  interface Jukebox {
    guid: string | null;
    tracks: S1ApiGen.JukeboxTrackBrief[] | null;
    currentTrack: S1ApiGen.JukeboxTrackBrief | null;
    volume: number | null;
    normalizedVolume: number | null;
    isPlaying: boolean | null;
    currentTrackTime: number | null;
    currentTrackOrderIndex: number | null;
    shuffle: boolean | null;
    repeatMode: S1ApiGen.JukeboxRepeatMode | null;
    sync: boolean | null;
    state: S1ApiGen.JukeboxStateBrief | null;
  }

  type JukeboxRepeatMode = 'None' | 'RepeatQueue' | 'RepeatTrack';

  /** S1API.Audio.JukeboxState, one level down: scalars only */
  interface JukeboxStateBrief {
    volume: number | null;
    normalizedVolume: number | null;
    isPlaying: boolean | null;
    currentTrackTime: number | null;
    currentTrackOrderIndex: number | null;
    shuffle: boolean | null;
    repeatMode: S1ApiGen.JukeboxRepeatMode | null;
    sync: boolean | null;
  }

  /** S1API.Audio.JukeboxTrack, one level down: scalars only */
  interface JukeboxTrackBrief {
    index: number | null;
    name: string | null;
    artist: string | null;
  }

  /** S1API.Vehicles.LandVehicle */
  interface LandVehicle {
    vehiclePrice: number | null;
    topSpeed: number | null;
    isPlayerOwned: boolean | null;
    isOccupied: boolean | null;
    color: S1ApiGen.VehicleColor | null;
    guid: string | null;
    storage: S1ApiGen.StorageInstanceBrief | null;
  }

  /** S1API.Vehicles.LandVehicle, one level down: scalars only */
  interface LandVehicleBrief {
    vehiclePrice: number | null;
    topSpeed: number | null;
    isPlayerOwned: boolean | null;
    isOccupied: boolean | null;
    color: S1ApiGen.VehicleColor | null;
    guid: string | null;
  }

  /** S1API.Property.LaunderingOperation, one level down: scalars only */
  interface LaunderingOperationBrief {
    amount: number | null;
    minutesSinceStarted: number | null;
    completionTimeInMinutes: number | null;
  }

  type LegalStatus = 'Legal' | 'Illegal';

  /** S1API.Deliveries.LoadingDock, one level down: scalars only */
  interface LoadingDockBrief {
    guid: string | null;
    name: string | null;
    isAcceptingItems: boolean | null;
    isDestroyed: boolean | null;
    isInUse: boolean | null;
  }

  /** S1API.Map.MapPOI */
  interface MapPOI {
    id: string | null;
    label: string | null;
    textVisibility: S1ApiGen.MapPOITextVisibility | null;
    rotateWithTarget: boolean | null;
    isVisible: boolean | null;
    isReady: boolean | null;
    isUIReady: boolean | null;
    isRemoved: boolean | null;
    position: { x: number; y: number; z: number } | null;
  }

  type MapPOITextVisibility = 'Off' | 'Always' | 'OnHover';

  /** S1API.Products.MethDefinition */
  interface MethDefinition {
    price: number | null;
    basePrice: number | null;
    marketValue: number | null;
    properties: S1ApiGen.PropertyBaseBrief[] | null;
    drugTypeValues: S1ApiGen.DrugType[] | null;
    primaryDrugType: S1ApiGen.DrugType | null;
    basePurchasePrice: number | null;
    resellMultiplier: number | null;
    isUnlocked: boolean | null;
    requiresLevelToPurchase: boolean | null;
    requiredRank: S1ApiGen.FullRankBrief | null;
    hasStationItem: boolean | null;
    id: string | null;
    name: string | null;
    description: string | null;
    stackLimit: number | null;
    category: S1ApiGen.ItemCategory | null;
    availableInDemo: boolean | null;
    legalStatus: S1ApiGen.LegalStatus | null;
    guid: string | null;
  }

  /** S1API.Entities.Voices.NPCVoiceDefinition */
  interface NPCVoiceDefinition {
    id: string | null;
    displayName: string | null;
  }

  /** S1API.Map.ParkingLotWrapper */
  interface ParkingLotWrapper {
    guid: string | null;
    gameObjectName: string | null;
    entryPointPosition: { x: number; y: number; z: number } | null;
    parkingSpotsCount: number | null;
  }

  /** S1API.Entities.Player */
  interface Player {
    isLocal: boolean | null;
    name: string | null;
    position: { x: number; y: number; z: number } | null;
    scale: number | null;
    crimeData: S1ApiGen.PlayerCrimeDataBrief | null;
    currentHealth: number | null;
    maxHealth: number | null;
    isDead: boolean | null;
    isInvincible: boolean | null;
    lastDrivenVehicle: S1ApiGen.LandVehicleBrief | null;
    isInVehicle: boolean | null;
    timeSinceVehicleExit: number | null;
    crouched: boolean | null;
    isReadyToSleep: boolean | null;
    isSkating: boolean | null;
    isSleeping: boolean | null;
    isRagdolled: boolean | null;
    isArrested: boolean | null;
    isTased: boolean | null;
    isUnconscious: boolean | null;
    currentProperty: S1ApiGen.PropertyWrapperBrief | null;
    lastVisitedProperty: S1ApiGen.PropertyWrapperBrief | null;
    currentRegion: S1ApiGen.Region | null;
  }

  /** S1API.Law.PlayerCrimeData, one level down: scalars only */
  interface PlayerCrimeDataBrief {
    currentPursuitLevel: S1ApiGen.PursuitLevel | null;
    lastKnownPosition: { x: number; y: number; z: number } | null;
    timeSinceSighted: number | null;
    bodySearchPending: boolean | null;
    evadedArrest: boolean | null;
  }

  /** S1API.Products.ProductDefinition */
  interface ProductDefinition {
    price: number | null;
    basePrice: number | null;
    marketValue: number | null;
    properties: S1ApiGen.PropertyBaseBrief[] | null;
    drugTypeValues: S1ApiGen.DrugType[] | null;
    primaryDrugType: S1ApiGen.DrugType | null;
    basePurchasePrice: number | null;
    resellMultiplier: number | null;
    isUnlocked: boolean | null;
    requiresLevelToPurchase: boolean | null;
    requiredRank: S1ApiGen.FullRankBrief | null;
    hasStationItem: boolean | null;
    id: string | null;
    name: string | null;
    description: string | null;
    stackLimit: number | null;
    category: S1ApiGen.ItemCategory | null;
    availableInDemo: boolean | null;
    legalStatus: S1ApiGen.LegalStatus | null;
    guid: string | null;
  }

  /** S1API.Products.ProductKind */
  interface ProductKind {
    id: string | null;
    compatibilityDrugType: S1ApiGen.DrugType | null;
  }

  /** S1API.Products.ProductKind, one level down: scalars only */
  interface ProductKindBrief {
    id: string | null;
    compatibilityDrugType: S1ApiGen.DrugType | null;
  }

  /** S1API.Products.ProductKindMetadata */
  interface ProductKindMetadata {
    productKind: S1ApiGen.ProductKindBrief | null;
    displayName: string | null;
    color: { r: number; g: number; b: number; a: number } | null;
    sortOrder: number | null;
    searchAliases: string[] | null;
    isVisibleInProductManager: boolean | null;
  }

  /** S1API.Properties.Interfaces.PropertyBase, one level down: scalars only */
  interface PropertyBaseBrief {
    id: string | null;
    name: string | null;
    description: string | null;
    tier: number | null;
    addictiveness: number | null;
  }

  /** S1API.Property.PropertyWrapper */
  interface PropertyWrapper {
    propertyName: string | null;
    propertyCode: string | null;
    price: number | null;
    isOwned: boolean | null;
    employeeCapacity: number | null;
    exteriorSpawnPosition: { x: number; y: number; z: number } | null;
    interiorSpawnPosition: { x: number; y: number; z: number } | null;
    loadingDockCount: number | null;
    loadingDocks: S1ApiGen.LoadingDockBrief[] | null;
    defaultRotation: number | null;
    availableInDemo: boolean | null;
    isContentCulled: boolean | null;
    employeeCount: number | null;
    buildableItemCount: number | null;
    npcSpawnPosition: { x: number; y: number; z: number } | null;
    employeeIdlePointCount: number | null;
  }

  /** S1API.Property.PropertyWrapper, one level down: scalars only */
  interface PropertyWrapperBrief {
    propertyName: string | null;
    propertyCode: string | null;
    price: number | null;
    isOwned: boolean | null;
    employeeCapacity: number | null;
    exteriorSpawnPosition: { x: number; y: number; z: number } | null;
    interiorSpawnPosition: { x: number; y: number; z: number } | null;
    loadingDockCount: number | null;
    defaultRotation: number | null;
    availableInDemo: boolean | null;
    isContentCulled: boolean | null;
    employeeCount: number | null;
    buildableItemCount: number | null;
    npcSpawnPosition: { x: number; y: number; z: number } | null;
    employeeIdlePointCount: number | null;
  }

  type PursuitLevel = 'None' | 'Investigating' | 'Arresting' | 'NonLethal' | 'Lethal';

  type QualityCalculationMethod = 'Additive' | 'Absolute';

  type Rank = 'StreetRat' | 'Hoodlum' | 'Peddler' | 'Hustler' | 'Bagman' | 'Enforcer' | 'ShotCaller' | 'BlockBoss' | 'Underlord' | 'Baron' | 'Kingpin';

  type Region = 'Northtown' | 'Westville' | 'Downtown' | 'Docks' | 'Suburbia' | 'Uptown';

  /** S1API.Casino.RideTheBusGame */
  interface RideTheBusGame {
    stage: S1ApiGen.RideTheBusStage | null;
    isQuestionActive: boolean | null;
    localBetMultiplier: number | null;
    multipliedLocalBet: number | null;
    remainingAnswerTime: number | null;
    isLocalPlayerInRound: boolean | null;
    readyPlayerCount: number | null;
    answeredPlayerCount: number | null;
    cards: S1ApiGen.CasinoCardSnapshotBrief[] | null;
    name: string | null;
    position: { x: number; y: number; z: number } | null;
    isOpen: boolean | null;
    isWaitingForPlayers: boolean | null;
    localBet: number | null;
    betLimits: S1ApiGen.CasinoBetLimitsBrief | null;
    players: S1ApiGen.CasinoPlayerSnapshotBrief[] | null;
  }

  type RideTheBusStage = 'WaitingForPlayers' | 'RedOrBlack' | 'HigherOrLower' | 'InsideOrOutside' | 'Suit';

  /** S1API.Avatar.Seat */
  interface Seat {
    label: string | null;
    hierarchyPath: string | null;
    seatSetName: string | null;
    indexInSet: number | null;
    sittingPosition: { x: number; y: number; z: number } | null;
    sittingRotation: { x: number; y: number; z: number; w: number } | null;
    accessPosition: { x: number; y: number; z: number } | null;
    accessRotation: { x: number; y: number; z: number; w: number } | null;
  }

  /** S1API.Shops.Shop */
  interface Shop {
    name: string | null;
  }

  /** S1API.Shops.Shop, one level down: scalars only */
  interface ShopBrief {
    name: string | null;
  }

  /** S1API.Products.ShroomAppearanceSettings, one level down: scalars only */
  interface ShroomAppearanceSettingsBrief {
    primaryColor: { rgba: number; r: number; g: number; b: number; a: number } | null;
    secondaryColor: { rgba: number; r: number; g: number; b: number; a: number } | null;
    hasSpots: boolean | null;
    spotsColor: { rgba: number; r: number; g: number; b: number; a: number } | null;
  }

  /** S1API.Products.ShroomDefinition */
  interface ShroomDefinition {
    appearanceSettings: S1ApiGen.ShroomAppearanceSettingsBrief | null;
    price: number | null;
    basePrice: number | null;
    marketValue: number | null;
    properties: S1ApiGen.PropertyBaseBrief[] | null;
    drugTypeValues: S1ApiGen.DrugType[] | null;
    primaryDrugType: S1ApiGen.DrugType | null;
    basePurchasePrice: number | null;
    resellMultiplier: number | null;
    isUnlocked: boolean | null;
    requiresLevelToPurchase: boolean | null;
    requiredRank: S1ApiGen.FullRankBrief | null;
    hasStationItem: boolean | null;
    id: string | null;
    name: string | null;
    description: string | null;
    stackLimit: number | null;
    category: S1ApiGen.ItemCategory | null;
    availableInDemo: boolean | null;
    legalStatus: S1ApiGen.LegalStatus | null;
    guid: string | null;
  }

  /** S1API.Casino.SlotMachine */
  interface SlotMachine {
    name: string | null;
    position: { x: number; y: number; z: number } | null;
    isSpinning: boolean | null;
    currentBet: number | null;
    availableBets: number[] | null;
  }

  type SlotOutcome = 'Jackpot' | 'BigWin' | 'SmallWin' | 'MiniWin' | 'NoWin';

  /** S1API.Casino.SlotSpinSnapshot */
  interface SlotSpinSnapshot {
    bet: number | null;
    symbols: S1ApiGen.SlotSymbol[] | null;
    wasStartedByLocalPlayer: boolean | null;
    outcome: S1ApiGen.SlotOutcome | null;
    winAmount: number | null;
  }

  type SlotSymbol = 'Cherry' | 'Lemon' | 'Grape' | 'Watermelon' | 'Bell' | 'Seven';

  /** S1API.Graffiti.SpraySurface */
  interface SpraySurface {
    guid: string | null;
    position: { x: number; y: number; z: number } | null;
    strokeCount: number | null;
    paintedPixelCount: number | null;
    hasDrawingBeenFinalized: boolean | null;
  }

  type StorageAccessSettings = 'Closed' | 'SinglePlayerOnly' | 'Full';

  /** S1API.Storage.StorageEntity, one level down: scalars only */
  interface StorageEntityBrief {
    name: string | null;
    slotCount: number | null;
    maxSlots: number | null;
    displayRowCount: number | null;
    slotsAreFilterable: boolean | null;
    itemId: string | null;
    isPlaceable: boolean | null;
    customName: string | null;
    hasCustomName: boolean | null;
  }

  /** S1API.Storage.StorageEventArgs */
  interface StorageEventArgs {
    storage: S1ApiGen.StorageEntityBrief | null;
    itemId: string | null;
  }

  /** S1API.Storages.StorageInstance */
  interface StorageInstance {
    name: string | null;
    subtitle: string | null;
    slotCount: number | null;
    itemCount: number | null;
    accessSettings: S1ApiGen.StorageAccessSettings | null;
    isOpened: boolean | null;
    slots: S1ApiGen.ItemSlotInstanceBrief[] | null;
  }

  /** S1API.Storages.StorageInstance, one level down: scalars only */
  interface StorageInstanceBrief {
    name: string | null;
    subtitle: string | null;
    slotCount: number | null;
    itemCount: number | null;
    accessSettings: S1ApiGen.StorageAccessSettings | null;
    isOpened: boolean | null;
  }

  /** S1API.Storage.StorageLoadingEventArgs */
  interface StorageLoadingEventArgs {
    itemCountBeingLoaded: number | null;
    currentSlotCount: number | null;
    needsMoreSlots: boolean | null;
    additionalSlotsNeeded: number | null;
    storage: S1ApiGen.StorageEntityBrief | null;
    itemId: string | null;
  }

  type VehicleColor = 'Black' | 'DarkGrey' | 'LightGrey' | 'White' | 'Yellow' | 'Orange' | 'Red' | 'DullRed' | 'Pink' | 'Purple' | 'Navy' | 'DarkBlue' | 'LightBlue' | 'Cyan' | 'LightGreen' | 'DarkGreen' | 'Custom';

  /** S1API.Vehicles.VehicleColorMetadata */
  interface VehicleColorMetadata {
    color: S1ApiGen.VehicleColor | null;
    displayName: string | null;
    materialColor: { r: number; g: number; b: number; a: number } | null;
    uiColor: { rgba: number; r: number; g: number; b: number; a: number } | null;
  }

  /** S1API.Law.VehiclePatrolRoute */
  interface VehiclePatrolRoute {
    routeName: string | null;
    waypointCount: number | null;
    startWaypointIndex: number | null;
    position: { x: number; y: number; z: number } | null;
  }

  /** S1API.Weather.WeatherState */
  interface WeatherState {
    sunny: number | null;
    cloudy: number | null;
    rainy: number | null;
    stormy: number | null;
    snowy: number | null;
    foggy: number | null;
    windy: number | null;
    hail: number | null;
    sleet: number | null;
  }

  /** S1API.Products.WeedDefinition */
  interface WeedDefinition {
    price: number | null;
    basePrice: number | null;
    marketValue: number | null;
    properties: S1ApiGen.PropertyBaseBrief[] | null;
    drugTypeValues: S1ApiGen.DrugType[] | null;
    primaryDrugType: S1ApiGen.DrugType | null;
    basePurchasePrice: number | null;
    resellMultiplier: number | null;
    isUnlocked: boolean | null;
    requiresLevelToPurchase: boolean | null;
    requiredRank: S1ApiGen.FullRankBrief | null;
    hasStationItem: boolean | null;
    id: string | null;
    name: string | null;
    description: string | null;
    stackLimit: number | null;
    category: S1ApiGen.ItemCategory | null;
    availableInDemo: boolean | null;
    legalStatus: S1ApiGen.LegalStatus | null;
    guid: string | null;
  }

  /** Every property of S1API.Building.BuildEvents, as `s1api.buildEvents.get()` returns it. */
  interface BuildEventsData {
  }

  /** s1api.buildEvents: S1API.Building.BuildEvents */
  interface BuildEventsArea extends Readonly<BuildEventsData> {
    /** Every property in one read, or null. */
    get(): BuildEventsData | null;
  }

  /** Every property of S1API.Building.BuildManager, as `s1api.buildManager.get()` returns it. */
  interface BuildManagerData {
    /** S1API.Building.BuildManager.IsBuilding */
    isBuilding: boolean | null;
  }

  /** s1api.buildManager: S1API.Building.BuildManager */
  interface BuildManagerArea extends Readonly<BuildManagerData> {
    /** Every property in one read, or null. */
    get(): BuildManagerData | null;
  }

  /** Every property of S1API.Map.Building, as `s1api.building.get()` returns it. */
  interface BuildingData {
  }

  /** s1api.building: S1API.Map.Building */
  interface BuildingArea extends Readonly<BuildingData> {
    /** Every property in one read, or null. */
    get(): BuildingData | null;
    /** S1API.Map.Building.GetAll() */
    getAll(): S1ApiGen.Building[] | null;
  }

  /** Every property of S1API.Property.BusinessManager, as `s1api.businessManager.get()` returns it. */
  interface BusinessManagerData {
  }

  /** s1api.businessManager: S1API.Property.BusinessManager */
  interface BusinessManagerArea extends Readonly<BusinessManagerData> {
    /** Every property in one read, or null. */
    get(): BusinessManagerData | null;
    /** S1API.Property.BusinessManager.GetAllBusinesses() */
    getAllBusinesses(): S1ApiGen.BusinessWrapper[] | null;
    /** S1API.Property.BusinessManager.GetOwnedBusinesses() */
    getOwnedBusinesses(): S1ApiGen.BusinessWrapper[] | null;
  }

  /** Every property of S1API.PhoneCalls.CallManager, as `s1api.callManager.get()` returns it. */
  interface CallManagerData {
    /** S1API.PhoneCalls.CallManager.PendingCount */
    pendingCount: number | null;
  }

  /** s1api.callManager: S1API.PhoneCalls.CallManager */
  interface CallManagerArea extends Readonly<CallManagerData> {
    /** Every property in one read, or null. */
    get(): CallManagerData | null;
  }

  /** Every property of S1API.Cartel.Cartel, as `s1api.cartel.get()` returns it. */
  interface CartelData {
    /** S1API.Cartel.Cartel.Instance */
    instance: S1ApiGen.Cartel | null;
  }

  /** s1api.cartel: S1API.Cartel.Cartel */
  interface CartelArea extends Readonly<CartelData> {
    /** Every property in one read, or null. */
    get(): CartelData | null;
  }

  /** Every property of S1API.Casino.CasinoGameRegistry, as `s1api.casinoGameRegistry.get()` returns it. */
  interface CasinoGameRegistryData {
  }

  /** s1api.casinoGameRegistry: S1API.Casino.CasinoGameRegistry */
  interface CasinoGameRegistryArea extends Readonly<CasinoGameRegistryData> {
    /** Every property in one read, or null. */
    get(): CasinoGameRegistryData | null;
    /** S1API.Casino.CasinoGameRegistry.GetBlackjackGames() */
    getBlackjackGames(): S1ApiGen.BlackjackGame[] | null;
    /** S1API.Casino.CasinoGameRegistry.GetRideTheBusGames() */
    getRideTheBusGames(): S1ApiGen.RideTheBusGame[] | null;
    /** S1API.Casino.CasinoGameRegistry.GetSlotMachines() */
    getSlotMachines(): S1ApiGen.SlotMachine[] | null;
    /** S1API.Casino.CasinoGameRegistry.GetTables() */
    getTables(): S1ApiGen.CasinoGameTable[] | null;
  }

  /** Every property of S1API.UI.CharacterCreatorManager, as `s1api.characterCreatorManager.get()` returns it. */
  interface CharacterCreatorManagerData {
    /** S1API.UI.CharacterCreatorManager.IsOpen */
    isOpen: boolean | null;
    /** S1API.UI.CharacterCreatorManager.ActiveSettings */
    activeSettings: S1ApiGen.BasicAvatarSettings | null;
  }

  /** s1api.characterCreatorManager: S1API.UI.CharacterCreatorManager */
  interface CharacterCreatorManagerArea extends Readonly<CharacterCreatorManagerData> {
    /** Every property in one read, or null. */
    get(): CharacterCreatorManagerData | null;
    /** S1API.UI.CharacterCreatorManager.GetAvailablePresets() */
    getAvailablePresets(): string[] | null;
  }

  /** Every property of S1API.Law.CheckpointManager, as `s1api.checkpointManager.get()` returns it. */
  interface CheckpointManagerData {
  }

  /** s1api.checkpointManager: S1API.Law.CheckpointManager */
  interface CheckpointManagerArea extends Readonly<CheckpointManagerData> {
    /** Every property in one read, or null. */
    get(): CheckpointManagerData | null;
    /** S1API.Law.CheckpointManager.GetAllCheckpointInfo() */
    getAllCheckpointInfo(): S1ApiGen.CheckpointInfo[] | null;
  }

  /** Every property of S1API.Stations.ChemistryStationRecipes, as `s1api.chemistryStationRecipes.get()` returns it. */
  interface ChemistryStationRecipesData {
  }

  /** s1api.chemistryStationRecipes: S1API.Stations.ChemistryStationRecipes */
  interface ChemistryStationRecipesArea extends Readonly<ChemistryStationRecipesData> {
    /** Every property in one read, or null. */
    get(): ChemistryStationRecipesData | null;
    /** S1API.Stations.ChemistryStationRecipes.GetAll() */
    getAll(): S1ApiGen.ChemistryStationRecipe[] | null;
  }

  /** Every property of S1API.Items.Clothing.ClothingMetadataCatalog, as `s1api.clothingMetadataCatalog.get()` returns it. */
  interface ClothingMetadataCatalogData {
    /** S1API.Items.Clothing.ClothingMetadataCatalog.Slots */
    slots: S1ApiGen.ClothingSlotMetadata[] | null;
    /** S1API.Items.Clothing.ClothingMetadataCatalog.Colors */
    colors: S1ApiGen.ClothingColorMetadata[] | null;
  }

  /** s1api.clothingMetadataCatalog: S1API.Items.Clothing.ClothingMetadataCatalog */
  interface ClothingMetadataCatalogArea extends Readonly<ClothingMetadataCatalogData> {
    /** Every property in one read, or null. */
    get(): ClothingMetadataCatalogData | null;
  }

  /** Every property of S1API.Console.ConsoleHelper, as `s1api.consoleHelper.get()` returns it. */
  interface ConsoleHelperData {
    /** S1API.Console.ConsoleHelper.RegisteredCommands */
    registeredCommands: { [key: string]: S1ApiGen.BaseConsoleCommand } | null;
  }

  /** s1api.consoleHelper: S1API.Console.ConsoleHelper */
  interface ConsoleHelperArea extends Readonly<ConsoleHelperData> {
    /** Every property in one read, or null. */
    get(): ConsoleHelperData | null;
  }

  /** Every property of S1API.Input.Controls, as `s1api.controls.get()` returns it. */
  interface ControlsData {
    /** S1API.Input.Controls.IsTyping */
    isTyping: boolean | null;
    /** S1API.Input.Controls.MotionAxis */
    motionAxis: { x: number; y: number } | null;
    /** S1API.Input.Controls.CameraAxis */
    cameraAxis: { x: number; y: number } | null;
    /** S1API.Input.Controls.MouseDelta */
    mouseDelta: { x: number; y: number } | null;
    /** S1API.Input.Controls.MousePosition */
    mousePosition: { x: number; y: number; z: number } | null;
    /** S1API.Input.Controls.MouseScrollDelta */
    mouseScrollDelta: number | null;
    /** S1API.Input.Controls.CurrentInputDevice */
    currentInputDevice: S1ApiGen.InputDeviceType | null;
    /** S1API.Input.Controls.ControllerComboActive */
    controllerComboActive: boolean | null;
    /** S1API.Input.Controls.VehicleDriveAxis */
    vehicleDriveAxis: number | null;
    /** S1API.Input.Controls.UINavigationDirection */
    uiNavigationDirection: { x: number; y: number } | null;
    /** S1API.Input.Controls.UICyclePanelDirection */
    uiCyclePanelDirection: { x: number; y: number } | null;
    /** S1API.Input.Controls.UITabNavigationPrimaryAxis */
    uiTabNavigationPrimaryAxis: number | null;
    /** S1API.Input.Controls.UITabNavigationSecondaryAxis */
    uiTabNavigationSecondaryAxis: number | null;
    /** S1API.Input.Controls.UIScrollbarAxis */
    uiScrollbarAxis: number | null;
    /** S1API.Input.Controls.UIMapNavigationDirection */
    uiMapNavigationDirection: { x: number; y: number } | null;
    /** S1API.Input.Controls.UIMapZoomAxis */
    uiMapZoomAxis: number | null;
    /** S1API.Input.Controls.UIModifyAmountIncrementTierOneAxis */
    uiModifyAmountIncrementTierOneAxis: number | null;
    /** S1API.Input.Controls.UIModifyAmountIncrementTierTwoAxis */
    uiModifyAmountIncrementTierTwoAxis: number | null;
    /** S1API.Input.Controls.UIModifyAmountIncrementTierThreeAxis */
    uiModifyAmountIncrementTierThreeAxis: number | null;
  }

  /** s1api.controls: S1API.Input.Controls */
  interface ControlsArea extends Readonly<ControlsData> {
    /** Every property in one read, or null. */
    get(): ControlsData | null;
    /** S1API.Input.Controls.GetCurrentInputDeviceIsKeyboardMouse() */
    getCurrentInputDeviceIsKeyboardMouse(): boolean | null;
    /** S1API.Input.Controls.GetCurrentInputDeviceIsGamepad() */
    getCurrentInputDeviceIsGamepad(): boolean | null;
  }

  /** Every property of S1API.Law.CurfewManager, as `s1api.curfewManager.get()` returns it. */
  interface CurfewManagerData {
    /** S1API.Law.CurfewManager.IsEnabled */
    isEnabled: boolean | null;
    /** S1API.Law.CurfewManager.IsCurrentlyActive */
    isCurrentlyActive: boolean | null;
    /** S1API.Law.CurfewManager.IsHardCurfewActive */
    isHardCurfewActive: boolean | null;
  }

  /** s1api.curfewManager: S1API.Law.CurfewManager */
  interface CurfewManagerArea extends Readonly<CurfewManagerData> {
    /** Every property in one read, or null. */
    get(): CurfewManagerData | null;
    /** S1API.Law.CurfewManager.IsWithinCurfewHours() */
    isWithinCurfewHours(): boolean | null;
    /** S1API.Law.CurfewManager.IsWithinHardCurfewHours() */
    isWithinHardCurfewHours(): boolean | null;
    /** S1API.Law.CurfewManager.MinutesUntilCurfew() */
    minutesUntilCurfew(): number | null;
    /** S1API.Law.CurfewManager.MinutesUntilCurfewEnds() */
    minutesUntilCurfewEnds(): number | null;
  }

  /** Every property of S1API.Products.CustomProductMultiplayer, as `s1api.customProductMultiplayer.get()` returns it. */
  interface CustomProductMultiplayerData {
    /** S1API.Products.CustomProductMultiplayer.MissingContentPolicy */
    missingContentPolicy: S1ApiGen.CustomProductMultiplayerPolicy | null;
  }

  /** s1api.customProductMultiplayer: S1API.Products.CustomProductMultiplayer */
  interface CustomProductMultiplayerArea extends Readonly<CustomProductMultiplayerData> {
    /** Every property in one read, or null. */
    get(): CustomProductMultiplayerData | null;
    /** S1API.Products.CustomProductMultiplayer.GetCompatibilityManifestHash() */
    getCompatibilityManifestHash(): string | null;
  }

  /** Every property of S1API.Cutscenes.CutsceneManager, as `s1api.cutsceneManager.get()` returns it. */
  interface CutsceneManagerData {
    /** S1API.Cutscenes.CutsceneManager.Active */
    active: S1ApiGen.CutsceneHandle | null;
    /** S1API.Cutscenes.CutsceneManager.IsPlaying */
    isPlaying: boolean | null;
  }

  /** s1api.cutsceneManager: S1API.Cutscenes.CutsceneManager */
  interface CutsceneManagerArea extends Readonly<CutsceneManagerData> {
    /** Every property in one read, or null. */
    get(): CutsceneManagerData | null;
  }

  /** Every property of S1API.DeadDrops.DeadDropManager, as `s1api.deadDropManager.get()` returns it. */
  interface DeadDropManagerData {
    /** S1API.DeadDrops.DeadDropManager.All */
    all: S1ApiGen.DeadDropInstance[] | null;
    /** S1API.DeadDrops.DeadDropManager.Empty */
    empty: S1ApiGen.DeadDropInstance[] | null;
  }

  /** s1api.deadDropManager: S1API.DeadDrops.DeadDropManager */
  interface DeadDropManagerArea extends Readonly<DeadDropManagerData> {
    /** Every property in one read, or null. */
    get(): DeadDropManagerData | null;
  }

  /** Every property of S1API.Map.DeliveryLocation, as `s1api.deliveryLocation.get()` returns it. */
  interface DeliveryLocationData {
  }

  /** s1api.deliveryLocation: S1API.Map.DeliveryLocation */
  interface DeliveryLocationArea extends Readonly<DeliveryLocationData> {
    /** Every property in one read, or null. */
    get(): DeliveryLocationData | null;
    /** S1API.Map.DeliveryLocation.GetAll() */
    getAll(): S1ApiGen.DeliveryLocation[] | null;
  }

  /** Every property of S1API.Deliveries.DeliveryRegistry, as `s1api.deliveryRegistry.get()` returns it. */
  interface DeliveryRegistryData {
  }

  /** s1api.deliveryRegistry: S1API.Deliveries.DeliveryRegistry */
  interface DeliveryRegistryArea extends Readonly<DeliveryRegistryData> {
    /** Every property in one read, or null. */
    get(): DeliveryRegistryData | null;
    /** S1API.Deliveries.DeliveryRegistry.GetAll() */
    getAll(): S1ApiGen.Delivery[] | null;
    /** S1API.Deliveries.DeliveryRegistry.GetHistory() */
    getHistory(): S1ApiGen.DeliveryReceipt[] | null;
  }

  /** Every property of S1API.Dialogues.DialogueChoicePaging, as `s1api.dialogueChoicePaging.get()` returns it. */
  interface DialogueChoicePagingData {
    /** S1API.Dialogues.DialogueChoicePaging.IsEnabled */
    isEnabled: boolean | null;
  }

  /** s1api.dialogueChoicePaging: S1API.Dialogues.DialogueChoicePaging */
  interface DialogueChoicePagingArea extends Readonly<DialogueChoicePagingData> {
    /** Every property in one read, or null. */
    get(): DialogueChoicePagingData | null;
  }

  /** Every property of S1API.ExternalHosting.ExternalAppCatalog, as `s1api.externalAppCatalog.get()` returns it. */
  interface ExternalAppCatalogData {
  }

  /** s1api.externalAppCatalog: S1API.ExternalHosting.ExternalAppCatalog */
  interface ExternalAppCatalogArea extends Readonly<ExternalAppCatalogData> {
    /** Every property in one read, or null. */
    get(): ExternalAppCatalogData | null;
    /** S1API.ExternalHosting.ExternalAppCatalog.GetAll() */
    getAll(): S1ApiGen.ExternalAppRegistration[] | null;
    /** S1API.ExternalHosting.ExternalAppCatalog.GetDiagnostics() */
    getDiagnostics(): S1ApiGen.ExternalAppDiagnostic[] | null;
  }

  /** Every property of S1API.Lifecycle.GameLifecycle, as `s1api.gameLifecycle.get()` returns it. */
  interface GameLifecycleData {
  }

  /** s1api.gameLifecycle: S1API.Lifecycle.GameLifecycle */
  interface GameLifecycleArea extends Readonly<GameLifecycleData> {
    /** Every property in one read, or null. */
    get(): GameLifecycleData | null;
  }

  /** Every property of S1API.Graffiti.GraffitiEvents, as `s1api.graffitiEvents.get()` returns it. */
  interface GraffitiEventsData {
  }

  /** s1api.graffitiEvents: S1API.Graffiti.GraffitiEvents */
  interface GraffitiEventsArea extends Readonly<GraffitiEventsData> {
    /** Every property in one read, or null. */
    get(): GraffitiEventsData | null;
  }

  /** Every property of S1API.Graffiti.GraffitiManager, as `s1api.graffitiManager.get()` returns it. */
  interface GraffitiManagerData {
    /** S1API.Graffiti.GraffitiManager.UntaggedSpraySurfaces */
    untaggedSpraySurfaces: S1ApiGen.SpraySurface[] | null;
  }

  /** s1api.graffitiManager: S1API.Graffiti.GraffitiManager */
  interface GraffitiManagerArea extends Readonly<GraffitiManagerData> {
    /** Every property in one read, or null. */
    get(): GraffitiManagerData | null;
    /** S1API.Graffiti.GraffitiManager.GetAllSpraySurfaces() */
    getAllSpraySurfaces(): S1ApiGen.SpraySurface[] | null;
    /** S1API.Graffiti.GraffitiManager.GetUntaggedSpraySurfaces() */
    getUntaggedSpraySurfaces(): S1ApiGen.SpraySurface[] | null;
  }

  /** Every property of S1API.Growing.GrowContainerAdditives, as `s1api.growContainerAdditives.get()` returns it. */
  interface GrowContainerAdditivesData {
  }

  /** s1api.growContainerAdditives: S1API.Growing.GrowContainerAdditives */
  interface GrowContainerAdditivesArea extends Readonly<GrowContainerAdditivesData> {
    /** Every property in one read, or null. */
    get(): GrowContainerAdditivesData | null;
    /** S1API.Growing.GrowContainerAdditives.GetAllowedAdditiveIds() */
    getAllowedAdditiveIds(): string[] | null;
  }

  /** Every property of S1API.Items.ItemManager, as `s1api.itemManager.get()` returns it. */
  interface ItemManagerData {
  }

  /** s1api.itemManager: S1API.Items.ItemManager */
  interface ItemManagerArea extends Readonly<ItemManagerData> {
    /** Every property in one read, or null. */
    get(): ItemManagerData | null;
    /** S1API.Items.ItemManager.GetAllItemDefinitions() */
    getAllItemDefinitions(): S1ApiGen.ItemDefinition[] | null;
  }

  /** Every property of S1API.Audio.JukeboxManager, as `s1api.jukeboxManager.get()` returns it. */
  interface JukeboxManagerData {
  }

  /** s1api.jukeboxManager: S1API.Audio.JukeboxManager */
  interface JukeboxManagerArea extends Readonly<JukeboxManagerData> {
    /** Every property in one read, or null. */
    get(): JukeboxManagerData | null;
    /** S1API.Audio.JukeboxManager.GetAll() */
    getAll(): S1ApiGen.Jukebox[] | null;
  }

  /** Every property of S1API.Law.LawController, as `s1api.lawController.get()` returns it. */
  interface LawControllerData {
    /** S1API.Law.LawController.Intensity */
    intensity: number | null;
    /** S1API.Law.LawController.InternalIntensity */
    internalIntensity: number | null;
    /** S1API.Law.LawController.IsUsingOverrideSettings */
    isUsingOverrideSettings: boolean | null;
  }

  /** s1api.lawController: S1API.Law.LawController */
  interface LawControllerArea extends Readonly<LawControllerData> {
    /** Every property in one read, or null. */
    get(): LawControllerData | null;
  }

  /** Every property of S1API.Law.LawManager, as `s1api.lawManager.get()` returns it. */
  interface LawManagerData {
    /** S1API.Law.LawManager.DispatchOfficerCount */
    dispatchOfficerCount: number | null;
    /** S1API.Law.LawManager.DispatchVehicleUseThreshold */
    dispatchVehicleUseThreshold: number | null;
    /** S1API.Law.LawManager.SearchTimeInvestigating */
    searchTimeInvestigating: number | null;
    /** S1API.Law.LawManager.SearchTimeArresting */
    searchTimeArresting: number | null;
    /** S1API.Law.LawManager.SearchTimeNonLethal */
    searchTimeNonLethal: number | null;
    /** S1API.Law.LawManager.SearchTimeLethal */
    searchTimeLethal: number | null;
    /** S1API.Law.LawManager.EscalationTimeArresting */
    escalationTimeArresting: number | null;
    /** S1API.Law.LawManager.EscalationTimeNonLethal */
    escalationTimeNonLethal: number | null;
    /** S1API.Law.LawManager.ActiveOfficerCount */
    activeOfficerCount: number | null;
  }

  /** s1api.lawManager: S1API.Law.LawManager */
  interface LawManagerArea extends Readonly<LawManagerData> {
    /** Every property in one read, or null. */
    get(): LawManagerData | null;
    /** S1API.Law.LawManager.GetAllFootPatrolRoutes() */
    getAllFootPatrolRoutes(): S1ApiGen.FootPatrolRoute[] | null;
    /** S1API.Law.LawManager.GetAllVehiclePatrolRoutes() */
    getAllVehiclePatrolRoutes(): S1ApiGen.VehiclePatrolRoute[] | null;
  }

  /** Every property of S1API.Leveling.LevelManager, as `s1api.levelManager.get()` returns it. */
  interface LevelManagerData {
    /** S1API.Leveling.LevelManager.Exists */
    exists: boolean | null;
    /** S1API.Leveling.LevelManager.Rank */
    rank: S1ApiGen.Rank | null;
    /** S1API.Leveling.LevelManager.Tier */
    tier: number | null;
    /** S1API.Leveling.LevelManager.XP */
    xp: number | null;
    /** S1API.Leveling.LevelManager.TotalXP */
    totalXP: number | null;
    /** S1API.Leveling.LevelManager.XPToNextTier */
    xpToNextTier: number | null;
    /** S1API.Leveling.LevelManager.CurrentRank */
    currentRank: S1ApiGen.FullRank | null;
  }

  /** s1api.levelManager: S1API.Leveling.LevelManager */
  interface LevelManagerArea extends Readonly<LevelManagerData> {
    /** Every property in one read, or null. */
    get(): LevelManagerData | null;
    /** S1API.Leveling.LevelManager.AddXP - true only when the app's mod called `AllowGameWrites("leveling")` */
    addXP(amount: number): boolean;
  }

  /** Every property of S1API.Map.MapPOIManager, as `s1api.mapPOIManager.get()` returns it. */
  interface MapPOIManagerData {
    /** S1API.Map.MapPOIManager.All */
    all: S1ApiGen.MapPOI[] | null;
  }

  /** s1api.mapPOIManager: S1API.Map.MapPOIManager */
  interface MapPOIManagerArea extends Readonly<MapPOIManagerData> {
    /** Every property in one read, or null. */
    get(): MapPOIManagerData | null;
  }

  /** Every property of S1API.Entities.NPC, as `s1api.npc.get()` returns it. */
  interface NpcData {
    /** S1API.Entities.NPC.CustomNpcsReady */
    customNpcsReady: boolean | null;
  }

  /** s1api.npc: S1API.Entities.NPC */
  interface NpcArea extends Readonly<NpcData> {
    /** Every property in one read, or null. */
    get(): NpcData | null;
  }

  /** Every property of S1API.Entities.Impostors.NPCImpostorCatalog, as `s1api.npcImpostorCatalog.get()` returns it. */
  interface NpcImpostorCatalogData {
  }

  /** s1api.npcImpostorCatalog: S1API.Entities.Impostors.NPCImpostorCatalog */
  interface NpcImpostorCatalogArea extends Readonly<NpcImpostorCatalogData> {
    /** Every property in one read, or null. */
    get(): NpcImpostorCatalogData | null;
    /** S1API.Entities.Impostors.NPCImpostorCatalog.GetAll() */
    getAll(): S1ApiGen.AvatarImpostorDefinition[] | null;
  }

  /** Every property of S1API.Entities.Voices.NPCVoiceCatalog, as `s1api.npcVoiceCatalog.get()` returns it. */
  interface NpcVoiceCatalogData {
    /** S1API.Entities.Voices.NPCVoiceCatalog.Cold */
    cold: S1ApiGen.NPCVoiceDefinition | null;
    /** S1API.Entities.Voices.NPCVoiceCatalog.Crackhead */
    crackhead: S1ApiGen.NPCVoiceDefinition | null;
    /** S1API.Entities.Voices.NPCVoiceCatalog.Female1 */
    female1: S1ApiGen.NPCVoiceDefinition | null;
    /** S1API.Entities.Voices.NPCVoiceCatalog.Female2 */
    female2: S1ApiGen.NPCVoiceDefinition | null;
    /** S1API.Entities.Voices.NPCVoiceCatalog.Goblin */
    goblin: S1ApiGen.NPCVoiceDefinition | null;
    /** S1API.Entities.Voices.NPCVoiceCatalog.Hippie */
    hippie: S1ApiGen.NPCVoiceDefinition | null;
    /** S1API.Entities.Voices.NPCVoiceCatalog.Joel */
    joel: S1ApiGen.NPCVoiceDefinition | null;
    /** S1API.Entities.Voices.NPCVoiceCatalog.Monotone */
    monotone: S1ApiGen.NPCVoiceDefinition | null;
    /** S1API.Entities.Voices.NPCVoiceCatalog.Redneck */
    redneck: S1ApiGen.NPCVoiceDefinition | null;
    /** S1API.Entities.Voices.NPCVoiceCatalog.Timid */
    timid: S1ApiGen.NPCVoiceDefinition | null;
    /** S1API.Entities.Voices.NPCVoiceCatalog.Tyler */
    tyler: S1ApiGen.NPCVoiceDefinition | null;
    /** S1API.Entities.Voices.NPCVoiceCatalog.All */
    all: S1ApiGen.NPCVoiceDefinition[] | null;
  }

  /** s1api.npcVoiceCatalog: S1API.Entities.Voices.NPCVoiceCatalog */
  interface NpcVoiceCatalogArea extends Readonly<NpcVoiceCatalogData> {
    /** Every property in one read, or null. */
    get(): NpcVoiceCatalogData | null;
  }

  /** Every property of S1API.Map.ParkingLotRegistry, as `s1api.parkingLotRegistry.get()` returns it. */
  interface ParkingLotRegistryData {
  }

  /** s1api.parkingLotRegistry: S1API.Map.ParkingLotRegistry */
  interface ParkingLotRegistryArea extends Readonly<ParkingLotRegistryData> {
    /** Every property in one read, or null. */
    get(): ParkingLotRegistryData | null;
    /** S1API.Map.ParkingLotRegistry.GetAll() */
    getAll(): S1ApiGen.ParkingLotWrapper[] | null;
  }

  /** Every property of S1API.Products.ProductKindMetadataRegistry, as `s1api.productKindMetadataRegistry.get()` returns it. */
  interface ProductKindMetadataRegistryData {
    /** S1API.Products.ProductKindMetadataRegistry.All */
    all: S1ApiGen.ProductKindMetadata[] | null;
  }

  /** s1api.productKindMetadataRegistry: S1API.Products.ProductKindMetadataRegistry */
  interface ProductKindMetadataRegistryArea extends Readonly<ProductKindMetadataRegistryData> {
    /** Every property in one read, or null. */
    get(): ProductKindMetadataRegistryData | null;
  }

  /** Every property of S1API.Products.ProductKindRegistry, as `s1api.productKindRegistry.get()` returns it. */
  interface ProductKindRegistryData {
    /** S1API.Products.ProductKindRegistry.All */
    all: S1ApiGen.ProductKind[] | null;
  }

  /** s1api.productKindRegistry: S1API.Products.ProductKindRegistry */
  interface ProductKindRegistryArea extends Readonly<ProductKindRegistryData> {
    /** Every property in one read, or null. */
    get(): ProductKindRegistryData | null;
  }

  /** Every property of S1API.Products.ProductManager, as `s1api.productManager.get()` returns it. */
  interface ProductManagerData {
    /** S1API.Products.ProductManager.DiscoveredProducts */
    discoveredProducts: S1ApiGen.ProductDefinition[] | null;
    /** S1API.Products.ProductManager.ListedProducts */
    listedProducts: S1ApiGen.ProductDefinition[] | null;
    /** S1API.Products.ProductManager.FavouritedProducts */
    favouritedProducts: S1ApiGen.ProductDefinition[] | null;
    /** S1API.Products.ProductManager.IsAcceptingOrders */
    isAcceptingOrders: boolean | null;
    /** S1API.Products.ProductManager.MethDiscovered */
    methDiscovered: boolean | null;
    /** S1API.Products.ProductManager.CocaineDiscovered */
    cocaineDiscovered: boolean | null;
    /** S1API.Products.ProductManager.ShroomsDiscovered */
    shroomsDiscovered: boolean | null;
  }

  /** s1api.productManager: S1API.Products.ProductManager */
  interface ProductManagerArea extends Readonly<ProductManagerData> {
    /** Every property in one read, or null. */
    get(): ProductManagerData | null;
  }

  /** Every property of S1API.Products.ProductPopulator, as `s1api.productPopulator.get()` returns it. */
  interface ProductPopulatorData {
  }

  /** s1api.productPopulator: S1API.Products.ProductPopulator */
  interface ProductPopulatorArea extends Readonly<ProductPopulatorData> {
    /** Every property in one read, or null. */
    get(): ProductPopulatorData | null;
    /** S1API.Products.ProductPopulator.GetAllProductDefinitions() */
    getAllProductDefinitions(): S1ApiGen.ProductDefinition[] | null;
    /** S1API.Products.ProductPopulator.GetWeedDefinitions() */
    getWeedDefinitions(): S1ApiGen.WeedDefinition[] | null;
    /** S1API.Products.ProductPopulator.GetMethDefinitions() */
    getMethDefinitions(): S1ApiGen.MethDefinition[] | null;
    /** S1API.Products.ProductPopulator.GetCocaineDefinitions() */
    getCocaineDefinitions(): S1ApiGen.CocaineDefinition[] | null;
    /** S1API.Products.ProductPopulator.GetShroomDefinitions() */
    getShroomDefinitions(): S1ApiGen.ShroomDefinition[] | null;
  }

  /** Every property of S1API.Property.PropertyManager, as `s1api.propertyManager.get()` returns it. */
  interface PropertyManagerData {
  }

  /** s1api.propertyManager: S1API.Property.PropertyManager */
  interface PropertyManagerArea extends Readonly<PropertyManagerData> {
    /** Every property in one read, or null. */
    get(): PropertyManagerData | null;
    /** S1API.Property.PropertyManager.GetAllProperties() */
    getAllProperties(): S1ApiGen.PropertyWrapper[] | null;
    /** S1API.Property.PropertyManager.GetOwnedProperties() */
    getOwnedProperties(): S1ApiGen.PropertyWrapper[] | null;
  }

  /** Every property of S1API.Avatar.Seat, as `s1api.seat.get()` returns it. */
  interface SeatData {
    /** S1API.Avatar.Seat.Count */
    count: number | null;
  }

  /** s1api.seat: S1API.Avatar.Seat */
  interface SeatArea extends Readonly<SeatData> {
    /** Every property in one read, or null. */
    get(): SeatData | null;
    /** S1API.Avatar.Seat.GetAll() */
    getAll(): S1ApiGen.Seat[] | null;
  }

  /** Every property of S1API.Shops.ShopManager, as `s1api.shopManager.get()` returns it. */
  interface ShopManagerData {
  }

  /** s1api.shopManager: S1API.Shops.ShopManager */
  interface ShopManagerArea extends Readonly<ShopManagerData> {
    /** Every property in one read, or null. */
    get(): ShopManagerData | null;
    /** S1API.Shops.ShopManager.GetAllShops() */
    getAllShops(): S1ApiGen.Shop[] | null;
  }

  /** Every property of S1API.Products.ShroomAppearanceSettings, as `s1api.shroomAppearanceSettings.get()` returns it. */
  interface ShroomAppearanceSettingsData {
    /** S1API.Products.ShroomAppearanceSettings.DefaultPrimaryColor */
    defaultPrimaryColor: { rgba: number; r: number; g: number; b: number; a: number } | null;
    /** S1API.Products.ShroomAppearanceSettings.DefaultSecondaryColor */
    defaultSecondaryColor: { rgba: number; r: number; g: number; b: number; a: number } | null;
    /** S1API.Products.ShroomAppearanceSettings.DefaultSpotsColor */
    defaultSpotsColor: { rgba: number; r: number; g: number; b: number; a: number } | null;
  }

  /** s1api.shroomAppearanceSettings: S1API.Products.ShroomAppearanceSettings */
  interface ShroomAppearanceSettingsArea extends Readonly<ShroomAppearanceSettingsData> {
    /** Every property in one read, or null. */
    get(): ShroomAppearanceSettingsData | null;
  }

  /** Every property of S1API.Storage.StorageEvents, as `s1api.storageEvents.get()` returns it. */
  interface StorageEventsData {
  }

  /** s1api.storageEvents: S1API.Storage.StorageEvents */
  interface StorageEventsArea extends Readonly<StorageEventsData> {
    /** Every property in one read, or null. */
    get(): StorageEventsData | null;
  }

  /** Every property of S1API.Storages.StorageManager, as `s1api.storageManager.get()` returns it. */
  interface StorageManagerData {
  }

  /** s1api.storageManager: S1API.Storages.StorageManager */
  interface StorageManagerArea extends Readonly<StorageManagerData> {
    /** Every property in one read, or null. */
    get(): StorageManagerData | null;
    /** S1API.Storages.StorageManager.GetAll() */
    getAll(): S1ApiGen.StorageInstance[] | null;
  }

  /** Every property of S1API.Temperature.TemperatureUtility, as `s1api.temperatureUtility.get()` returns it. */
  interface TemperatureUtilityData {
    /** S1API.Temperature.TemperatureUtility.TemperatureSystemEnabled */
    temperatureSystemEnabled: boolean | null;
  }

  /** s1api.temperatureUtility: S1API.Temperature.TemperatureUtility */
  interface TemperatureUtilityArea extends Readonly<TemperatureUtilityData> {
    /** Every property in one read, or null. */
    get(): TemperatureUtilityData | null;
  }

  /** Every property of S1API.GameTime.TimeManager, as `s1api.timeManager.get()` returns it. */
  interface TimeManagerData {
    /** S1API.GameTime.TimeManager.CurrentDay */
    currentDay: S1ApiGen.Day | null;
    /** S1API.GameTime.TimeManager.ElapsedDays */
    elapsedDays: number | null;
    /** S1API.GameTime.TimeManager.CurrentTime */
    currentTime: number | null;
    /** S1API.GameTime.TimeManager.IsNight */
    isNight: boolean | null;
    /** S1API.GameTime.TimeManager.IsEndOfDay */
    isEndOfDay: boolean | null;
    /** S1API.GameTime.TimeManager.SleepInProgress */
    sleepInProgress: boolean | null;
    /** S1API.GameTime.TimeManager.NormalizedTime */
    normalizedTime: number | null;
    /** S1API.GameTime.TimeManager.Playtime */
    playtime: number | null;
  }

  /** s1api.timeManager: S1API.GameTime.TimeManager */
  interface TimeManagerArea extends Readonly<TimeManagerData> {
    /** Every property in one read, or null. */
    get(): TimeManagerData | null;
    /** S1API.GameTime.TimeManager.GetFormatted12HourTime() */
    getFormatted12HourTime(): string | null;
  }

  /** Every property of S1API.Vehicles.VehicleColorCatalog, as `s1api.vehicleColorCatalog.get()` returns it. */
  interface VehicleColorCatalogData {
    /** S1API.Vehicles.VehicleColorCatalog.Colors */
    colors: S1ApiGen.VehicleColorMetadata[] | null;
  }

  /** s1api.vehicleColorCatalog: S1API.Vehicles.VehicleColorCatalog */
  interface VehicleColorCatalogArea extends Readonly<VehicleColorCatalogData> {
    /** Every property in one read, or null. */
    get(): VehicleColorCatalogData | null;
  }

  /** Every property of S1API.Vehicles.VehicleRegistry, as `s1api.vehicleRegistry.get()` returns it. */
  interface VehicleRegistryData {
  }

  /** s1api.vehicleRegistry: S1API.Vehicles.VehicleRegistry */
  interface VehicleRegistryArea extends Readonly<VehicleRegistryData> {
    /** Every property in one read, or null. */
    get(): VehicleRegistryData | null;
    /** S1API.Vehicles.VehicleRegistry.GetAll() */
    getAll(): S1ApiGen.LandVehicle[] | null;
  }

  /** Every property of S1API.Weather.WeatherManager, as `s1api.weatherManager.get()` returns it. */
  interface WeatherManagerData {
    /** S1API.Weather.WeatherManager.Current */
    current: S1ApiGen.WeatherState | null;
    /** S1API.Weather.WeatherManager.KnownSequenceIds */
    knownSequenceIds: string[] | null;
  }

  /** s1api.weatherManager: S1API.Weather.WeatherManager */
  interface WeatherManagerArea extends Readonly<WeatherManagerData> {
    /** Every property in one read, or null. */
    get(): WeatherManagerData | null;
  }
}

interface S1ApiMoneyArea {
  /** S1API.Money.Money.GetNetWorth() */
  getNetWorth(): number | null;
  /** S1API.Money.Money.GetCashBalance() */
  getCashBalance(): number | null;
  /** S1API.Money.Money.GetOnlineBalance() */
  getOnlineBalance(): number | null;
  /** S1API.Money.Money.ChangeCashBalance - true only when the app's mod called `AllowGameWrites("money")` */
  changeCashBalance(amount: number, visualizeChange?: boolean, playCashSound?: boolean): boolean;
}

interface S1ApiPlayerArea {
  /** S1API.Entities.Player.Local */
  readonly local: S1ApiGen.Player | null;
}

interface S1Api {
  readonly buildEvents: S1ApiGen.BuildEventsArea;
  readonly buildManager: S1ApiGen.BuildManagerArea;
  readonly building: S1ApiGen.BuildingArea;
  readonly businessManager: S1ApiGen.BusinessManagerArea;
  readonly callManager: S1ApiGen.CallManagerArea;
  readonly cartel: S1ApiGen.CartelArea;
  readonly casinoGameRegistry: S1ApiGen.CasinoGameRegistryArea;
  readonly characterCreatorManager: S1ApiGen.CharacterCreatorManagerArea;
  readonly checkpointManager: S1ApiGen.CheckpointManagerArea;
  readonly chemistryStationRecipes: S1ApiGen.ChemistryStationRecipesArea;
  readonly clothingMetadataCatalog: S1ApiGen.ClothingMetadataCatalogArea;
  readonly consoleHelper: S1ApiGen.ConsoleHelperArea;
  readonly controls: S1ApiGen.ControlsArea;
  readonly curfewManager: S1ApiGen.CurfewManagerArea;
  readonly customProductMultiplayer: S1ApiGen.CustomProductMultiplayerArea;
  readonly cutsceneManager: S1ApiGen.CutsceneManagerArea;
  readonly deadDropManager: S1ApiGen.DeadDropManagerArea;
  readonly deliveryLocation: S1ApiGen.DeliveryLocationArea;
  readonly deliveryRegistry: S1ApiGen.DeliveryRegistryArea;
  readonly dialogueChoicePaging: S1ApiGen.DialogueChoicePagingArea;
  readonly externalAppCatalog: S1ApiGen.ExternalAppCatalogArea;
  readonly gameLifecycle: S1ApiGen.GameLifecycleArea;
  readonly graffitiEvents: S1ApiGen.GraffitiEventsArea;
  readonly graffitiManager: S1ApiGen.GraffitiManagerArea;
  readonly growContainerAdditives: S1ApiGen.GrowContainerAdditivesArea;
  readonly itemManager: S1ApiGen.ItemManagerArea;
  readonly jukeboxManager: S1ApiGen.JukeboxManagerArea;
  readonly lawController: S1ApiGen.LawControllerArea;
  readonly lawManager: S1ApiGen.LawManagerArea;
  readonly levelManager: S1ApiGen.LevelManagerArea;
  readonly mapPOIManager: S1ApiGen.MapPOIManagerArea;
  readonly npc: S1ApiGen.NpcArea;
  readonly npcImpostorCatalog: S1ApiGen.NpcImpostorCatalogArea;
  readonly npcVoiceCatalog: S1ApiGen.NpcVoiceCatalogArea;
  readonly parkingLotRegistry: S1ApiGen.ParkingLotRegistryArea;
  readonly productKindMetadataRegistry: S1ApiGen.ProductKindMetadataRegistryArea;
  readonly productKindRegistry: S1ApiGen.ProductKindRegistryArea;
  readonly productManager: S1ApiGen.ProductManagerArea;
  readonly productPopulator: S1ApiGen.ProductPopulatorArea;
  readonly propertyManager: S1ApiGen.PropertyManagerArea;
  readonly seat: S1ApiGen.SeatArea;
  readonly shopManager: S1ApiGen.ShopManagerArea;
  readonly shroomAppearanceSettings: S1ApiGen.ShroomAppearanceSettingsArea;
  readonly storageEvents: S1ApiGen.StorageEventsArea;
  readonly storageManager: S1ApiGen.StorageManagerArea;
  readonly temperatureUtility: S1ApiGen.TemperatureUtilityArea;
  readonly timeManager: S1ApiGen.TimeManagerArea;
  readonly vehicleColorCatalog: S1ApiGen.VehicleColorCatalogArea;
  readonly vehicleRegistry: S1ApiGen.VehicleRegistryArea;
  readonly weatherManager: S1ApiGen.WeatherManagerArea;
}

interface S1ApiTopics {
  /** S1API.Building.BuildEvents.OnGridItemCreated */
  'buildEvents.onGridItemCreated': S1ApiGen.BuildEventArgs;
  /** S1API.Building.BuildEvents.OnSurfaceItemCreated */
  'buildEvents.onSurfaceItemCreated': S1ApiGen.BuildEventArgs;
  /** S1API.Building.BuildEvents.OnBuildableItemInitialized */
  'buildEvents.onBuildableItemInitialized': S1ApiGen.BuildEventArgs;
  /** S1API.Casino.CasinoGameRegistry.BlackjackStageChanged */
  'casinoGameRegistry.blackjackStageChanged': [S1ApiGen.BlackjackGame | null, S1ApiGen.BlackjackStage | null, S1ApiGen.BlackjackStage | null];
  /** S1API.Casino.CasinoGameRegistry.BlackjackRoundStarted */
  'casinoGameRegistry.blackjackRoundStarted': S1ApiGen.BlackjackGame;
  /** S1API.Casino.CasinoGameRegistry.BlackjackRoundEnded */
  'casinoGameRegistry.blackjackRoundEnded': S1ApiGen.BlackjackGame;
  /** S1API.Casino.CasinoGameRegistry.RideTheBusStageChanged */
  'casinoGameRegistry.rideTheBusStageChanged': [S1ApiGen.RideTheBusGame | null, S1ApiGen.RideTheBusStage | null, S1ApiGen.RideTheBusStage | null];
  /** S1API.Casino.CasinoGameRegistry.RideTheBusRoundStarted */
  'casinoGameRegistry.rideTheBusRoundStarted': S1ApiGen.RideTheBusGame;
  /** S1API.Casino.CasinoGameRegistry.RideTheBusRoundEnded */
  'casinoGameRegistry.rideTheBusRoundEnded': S1ApiGen.RideTheBusGame;
  /** S1API.Casino.CasinoGameRegistry.SlotSpinStarted */
  'casinoGameRegistry.slotSpinStarted': [S1ApiGen.SlotMachine | null, S1ApiGen.SlotSpinSnapshot | null];
  /** S1API.Casino.CasinoGameRegistry.SlotSpinCompleted */
  'casinoGameRegistry.slotSpinCompleted': [S1ApiGen.SlotMachine | null, S1ApiGen.SlotSpinSnapshot | null];
  /** S1API.UI.CharacterCreatorManager.OnOpened */
  'characterCreatorManager.onOpened': null;
  /** S1API.UI.CharacterCreatorManager.OnClosed */
  'characterCreatorManager.onClosed': null;
  /** S1API.UI.CharacterCreatorManager.OnCompleted */
  'characterCreatorManager.onCompleted': S1ApiGen.BasicAvatarSettings;
  /** S1API.Deliveries.DeliveryRegistry.Created */
  'deliveryRegistry.created': S1ApiGen.Delivery;
  /** S1API.Deliveries.DeliveryRegistry.StatusChanged */
  'deliveryRegistry.statusChanged': [S1ApiGen.Delivery | null, S1ApiGen.DeliveryStatus | null, S1ApiGen.DeliveryStatus | null];
  /** S1API.Deliveries.DeliveryRegistry.Completed */
  'deliveryRegistry.completed': S1ApiGen.Delivery;
  /** S1API.ExternalHosting.ExternalAppCatalog.Changed */
  'externalAppCatalog.changed': null;
  /** S1API.Lifecycle.GameLifecycle.OnPreLoad */
  'gameLifecycle.onPreLoad': null;
  /** S1API.Lifecycle.GameLifecycle.OnLoadComplete */
  'gameLifecycle.onLoadComplete': null;
  /** S1API.Lifecycle.GameLifecycle.OnPreSceneChange */
  'gameLifecycle.onPreSceneChange': null;
  /** S1API.Lifecycle.GameLifecycle.OnSaveInfoLoaded */
  'gameLifecycle.onSaveInfoLoaded': null;
  /** S1API.Lifecycle.GameLifecycle.OnSaveStart */
  'gameLifecycle.onSaveStart': null;
  /** S1API.Lifecycle.GameLifecycle.OnSaveComplete */
  'gameLifecycle.onSaveComplete': null;
  /** S1API.Graffiti.GraffitiEvents.GraffitiCompleted */
  'graffitiEvents.graffitiCompleted': S1ApiGen.SpraySurface;
  /** S1API.Leveling.LevelManager.OnXPChanged */
  'levelManager.onXPChanged': [S1ApiGen.FullRank | null, S1ApiGen.FullRank | null];
  /** S1API.Leveling.LevelManager.OnRankUp */
  'levelManager.onRankUp': [S1ApiGen.FullRank | null, S1ApiGen.FullRank | null];
  /** S1API.Money.Money.OnBalanceChanged */
  'money.onBalanceChanged': null;
  /** S1API.Entities.Player.PlayerSpawned */
  'player.playerSpawned': S1ApiGen.Player;
  /** S1API.Entities.Player.LocalPlayerSpawned */
  'player.localPlayerSpawned': S1ApiGen.Player;
  /** S1API.Entities.Player.PlayerDespawned */
  'player.playerDespawned': S1ApiGen.Player;
  /** S1API.Storage.StorageEvents.OnStorageCreated */
  'storageEvents.onStorageCreated': S1ApiGen.StorageEventArgs;
  /** S1API.Storage.StorageEvents.OnStorageLoading */
  'storageEvents.onStorageLoading': S1ApiGen.StorageLoadingEventArgs;
  /** S1API.Storage.StorageEvents.OnStorageOpening */
  'storageEvents.onStorageOpening': S1ApiGen.StorageEventArgs;
  /** S1API.GameTime.TimeManager.OnHourPass */
  'timeManager.onHourPass': null;
  /** S1API.GameTime.TimeManager.OnDayPass */
  'timeManager.onDayPass': null;
  /** S1API.GameTime.TimeManager.OnWeekPass */
  'timeManager.onWeekPass': null;
  /** S1API.GameTime.TimeManager.OnSleepStart */
  'timeManager.onSleepStart': null;
  /** S1API.GameTime.TimeManager.OnSleepEnd */
  'timeManager.onSleepEnd': number;
  /** S1API.GameTime.TimeManager.OnTick */
  'timeManager.onTick': null;
  /** S1API.Weather.WeatherManager.OnWeatherChanged */
  'weatherManager.onWeatherChanged': S1ApiGen.WeatherState;
}
