# HoF2 VR — design reference (condensed from code research, 2026-10-10)
Paths are relative to reference/decompiled/Assembly-CSharp/.

## Rendering / platform decisions
- Native Linux build, Unity OpenVR device -> OpenComposite -> OpenXR (WiVRn / Monado). Use **OpenGL Core**
  (`-force-glcore`): Vulkan worked but a stereo-camera MSAA mismatch caused an amdgpu page fault + GPU
  reset (killed Monado and Steam). GL is the safe default.
- `libmonosgen-2.0.so -> Hand of Fate 2_Data/Mono/x86_64/libmono.so` symlink in game root: stops MonoMod
  loading libMonoPosixHelper.so RTLD_GLOBAL (its eglib g_* exports broke GTK2 ScreenSelector.so).
- Window fixed 1920x1080 windowed via launch args; ResolutionManager.SetResolution + UIGraphicsSettings*
  blocked in VR (swapchain rebuilds during VR deadlocked Vulkan present).
- TAA must be off (overwrites projectionMatrix in OnPreCull; crashed). DOF off. DistanceAndHeightFog and
  WorldDarken use single-eye frustum math (check visually). CameraFade = OnRenderImage colour blend.
- Post settings: QualitySettingsManager.Instance.CameraSettings (AA/DOF/SSAO/ChromaticAberration) +
  ApplyCurrentQualitySettings(false). Global: HoFUberPostProcessing.DisableUberPostStack, DistanceAndHeightFog.DisableFog.

## Cameras (two directors, no manager)
- CardCamera (table): moves the PARENT of the UI3D camera ('Card Table 3DUI Camera'); shots stack (Map,
  ChallengeMap, Dice, Pendulum, Wheel, Reveal pans, Attract wanders, EnterCombat spline, Intro animated).
  UpdateCameraProperties() each LateUpdate; UsePlayerCamera copies PlayerCamera pose in combat.
  UI3D camera is under a NGUI UIRoot scaled 2/activeHeight -> stereo IPD would be scaled! (verify/fix)
- PlayerCamera (combat; Defiant.Cameras): own transform; LateUpdate framing; moves+restores transform
  internally; CurrentCameraPosition/Rotation; ApplyCameraModifiers (shake), ApplyOverrides (AnimatedCamera,
  GloryCamera finisher w/ slow-mo), UpdateListener (audio). Created per combat scene (destroyed after).
- StartScreen camera animated by sequence. MeshWormhole camera = table<->combat tunnel.
- Game states: Game.Instance.OnGameStateEntered/Exited; GameState.Enter()/Exit() patchable.
  Menu: Init, StartScreen, Profile, Start. Table: Intro, Attract, ChallengeMap (hub), ChallengeSetup, Map
  (dungeon), TokenUnlock, Post. Combat: Level_In, Level_Play, Level_Pause, Level_Out. Boss: BossBattle_In/Out.
  Customisation_*. Pause: MenuManager.Paused (timeScale 0).
- CombatEncounter statics: OnTransitionIn, OnTransitionInComplete, OnStarted, OnComplete, OnFailed (cleared
  on GameResetHelper reset -> resubscribe). Game.ClearScene destroys all GameObjects -> HideFlags.DontSave.

## UI (NGUI, modified copy)
- UI2D: 'UIManager/UI Root (2D)/Camera' ortho depth 3 layer 17 -> redirected to RT 1920x1080 (panel).
- UICamera.ProcessMouse reads Input.mousePosition; returns if !Cursor.visible. UICamera.Raycast(Vector3)
  (1031+) iterates UICamera.list: World_3D / UI_3D; sets private static mRayHitObject (RayHitObject),
  lastHit, lastWorldRay, lastWorldPosition. -> laser pointer: prefix Raycast.
- Clicks: InControl.MouseBindingSource.SafeGetMouseButton(int) choke point (LMB=Select, RMB=Cancel, MMB=Inspect).
- Selection system: UISelectionManager / UISelection / UISelectable (SelectAction, CancelAction). Mouse class
  Select requires RayHitObject == hovered selectable. UISelection.TrySelect(s)/TryCancel.
- Nav bar F1/F2/F3 via UIInputManager.OnInputButton (works in any device mode). Pause:
  MenuManager.Instance.PauseMenuManager.SetState(Pause).
- 2D widgets following 3D (project via Camera.main / UI3D cam): FloatingHealthBar, InputHintsUI (counter
  prompts), Indicator (off-screen arrows), FloatingText (damage), UIAttachment, UIShopValue, CardInfoPanel.
- Cards: world-space NGUI geometry on CardTemplate (layer 16). CardZoom moves the card (no camera zoom).

## Mini-games (all NodeCanvas ObservableTask, static RunningTask)
- ChanceCardTask: pick 1 of 4 -> UISelection.TrySelect(card.Selectable) on UI3D.CardChoice cards.
- DiceRollTask + DiceRoller: mouse drag uses Input.mousePosition (DiceRoller.cs:400,493) and GetMouseButtonUp(0) (509);
  SetReady() throws. Physical throw possible: set m_throwStrength/m_dragVelocity from hand velocity.
- PendulumTask: DefaultSelectable.SelectAction = EvaluatePendulum (stop). WheelSpinTask: OnWheelSelect stops.

## Input (gameplay) — Defiant.InputManager over InControl
- CombatActionSet (pad): Attack=X, Stun=B, Weapon=RT, Artifact=RB, Counter=Y, Dodge=A (hold=run), Prone=LT,
  Companion=LB, Move=LS, Orbit=RS. UI: Select=A, Cancel=B, F1=Back, F2=Y, F3=X, Next/Prev=RB/LB, Pause=Start.
- Rising-edge presses; one global "last pressed" slot; 0.5 s buffer; ButtonConsumer consumes. Combat buttons
  only in GameState_Level_Play. NEVER press L3/R3 (bug reporter / debug GUI).
- Virtual pad: subclass InControl.InputDevice (XboxOneInputDevice template), AttachDevice once
  InputManager.IsSetup; pin CombatActionSet.Device/UIActionSet.Device in postfix of
  Defiant.InputManager.InitDefaultBindings; prefix private OnInputBy(InputDeviceClass) to keep Controller.
  Vibrate() override -> haptics. Re-attach if !IsAttached.
- Movement is camera-relative: InputManager uses PlayerCamera.transform.localToWorldMatrix (4-frame history).

## Combat
- PlayerController.Instance (Controller): Model, Animator(Proxy), Targetable, NavMeshAgent. Actions via
  behaviour tree: ActionMove, ActionMelee(Attack/Stun/Counter/Ability), ActionMeleeFlurry (mash Stun 0.3 s
  vs armour), ActionMeleeProneFinisher, ActionCounter, ActionDodge (5 m scripted), ActionAbility, ActionSnared.
- Hits: animation events "hit" -> OnHit -> CombatEffect.Apply (no colliders). Targeting: TargetUtils.FindTarget
  (dir from relative stick; neutral -> 360 extra cone). Steer target by prefix on FindTarget(ref direction).
- Counter: enemy running IActionParry with ParryTime.HasValue && CanCounter within Combat.DefaultAttackRange.
  Telegraph hook: CombatUtils.Start/StopMeleeEffect(EffectSet, Model, bool blockable) and ranged variants.
- Rig anchors: Model.FindMultiAttachNode("eyes") midpoint; attach nodes "weapon", "parry", "artefact_hand".
  Equipment: Inventory.Weapon.Model/AllModels, Shield.Model, Helm, Armour, Artifact (Equipment.cs).
  PlayerSkin groups Body/Head/Helm/Hood (renderer lists) -> hide with ShadowsOnly.
- Enemies: CombatManager.Instance.Targets (Team Enemy, !IsDead, !Hidden); Destroyable health.
- HUD: UI2D CombatHUD (health radial, weapon/artifact/bash charge), floating bars via Camera.main.
- Slow-mo: TimeDilation on hits/counters -> rig must use unscaled time.
