using System.Numerics;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ImGui;
using Silk.NET.Windowing;
using ImGuiNET;
using SdlApi = Silk.NET.SDL.Sdl;

namespace SaveLocker.Agent.Linux.Ui;

/// <summary>
/// `savelocker ui` — the gamepad-native Game Mode surface (Linux-Agent-Streamline.md §3). All three
/// on-device gates passed 2026-07-24: renders under gamescope (§3.3.1), Steam Input delivers a
/// gamepad (§3.3.2), tarball delta is single-digit MB (§3.1).
///
/// It is a *view*, not a second frontend: it drives the same Agent.Core services the daemon's HTTP
/// API wraps (<see cref="LinuxGameScanner"/>, <see cref="PathBrowser"/>, <see cref="Enroller"/>,
/// <see cref="AgentConfig"/>) in-process — no second API client, no duplicated sync logic.
/// </summary>
sealed class UiApp
{
    private enum Screen { Status, AddGame, SetFolder, LaunchSetup, Settings, Gallery }

    private readonly AgentConfig _config;
    private readonly LinuxGameScanner _scanner;
    private readonly PathBrowser _browser;
    private readonly LaunchCommandDto _launch;
    private readonly SettingsScreen _settings;
    private readonly LeaseWarningStore _leaseWarnings;

    // Warnings are re-read on a timer rather than every frame: the daemon and the launch wrapper
    // write this file from other processes, so it has to be polled, but it changes at most twice
    // per game launch and a per-frame read would hit the disk 60 times a second.
    private IReadOnlyList<LeaseWarningStore.Entry> _warnings = Array.Empty<LeaseWarningStore.Entry>();
    private long _warningsReadAt;
    private const long WarningsPollMs = 2000;

    private IWindow _window = null!;
    private GL _gl = null!;
    private IInputContext _input = null!;
    private ImGuiController _controller = null!;
    private readonly HashSet<int> _hookedPads = new();

    // Nav is driven by ButtonDown EVENTS, not a per-frame poll: at 30 fps a quick D-pad tap can
    // begin and end inside one frame, so polling drops it and nav feels dead. Events never miss.
    // Each queued press is replayed as a single-frame ImGui key pulse — reliable and flicker-free.
    private readonly object _navLock = new();
    private readonly List<ImGuiKey> _navQueue = new();
    private ImGuiKey[] _navDownLastFrame = Array.Empty<ImGuiKey>();
    // Held D-pad directions, for hold-to-repeat scrolling: key -> when pressed / when last repeated.
    private readonly Dictionary<ImGuiKey, long> _heldSince = new();
    private readonly Dictionary<ImGuiKey, long> _lastRepeat = new();
    private const long RepeatDelayMs = 400;
    private const long RepeatRateMs = 90;

    private Screen _screen = Screen.Status;

    // Cross-fade state. _renderedScreen lags _screen by exactly the frame in which navigation
    // happened, which is how the change is detected without every caller having to announce it.
    private Screen _renderedScreen = Screen.Status;
    private float _screenFade = 1f;
    // 0.30 s, up from 0.16. Ease-out cubic spends most of its time near full opacity, so at 160 ms the
    // visible part of the transition was roughly 80 ms — it measured as working and went unnoticed on
    // the Deck across a whole session, same failure as the focus ring's alpha-only pulse.
    private const float ScreenFadeSeconds = 0.30f;

    /// <summary>Put the nav cursor on the rail on the first frame, so the D-pad works immediately.</summary>
    private bool _focusRailOnce = true;

    // Which pane owns the cursor. Only that pane is navigable, so a directional move can never
    // wander across the boundary by accident; crossing panes is always deliberate.
    private enum Zone { Rail, Content }
    private Zone _focusZone = Zone.Rail;

    private uint _activeRailId;        // rail entry for the screen being shown
    private uint _bestContentId;       // topmost-leftmost focusable control in the content pane
    // Right pressed while the content had nothing focusable yet — a scan still running, so the pane
    // is just a spinner. The intent is held until a control appears rather than being swallowed.
    private int _pendingCrossFrames;
    private const int PendingCrossFrames = 1800;   // ~30 s; a cold scan can take a while
    private bool _navLeftFired, _navRightFired, _navBackFired;
    private int _strandedFrames;

    // Scripted navigation, for verification. Gamepad nav is the single hardest thing to test off a
    // Deck, and it is exactly where the bugs are — so `--nav right,down,a` replays presses through
    // the same queue a real pad feeds, and `--screenshot` then captures where the cursor landed.
    private readonly Queue<ImGuiKey> _navScript = new();
    private int _navScriptCooldown;
    private const int NavScriptFrameGap = 6;   // ImGui needs a few frames to settle each move
    private const int NavScriptStartFrame = 15;

    // Add-game state. The candidate list is a mutable copy so a browsed folder can be written back
    // onto a candidate before enrollment, exactly as AgentApiServer rewrites its _candidateCache.
    private Task<IReadOnlyList<ScanCandidate>>? _scanTask;
    private readonly List<ScanCandidate> _candidates = new();
    private readonly HashSet<int> _selected = new();
    private Task<(int enrolled, int skipped)>? _enrollTask;
    private string _addStatus = "";

    /// <summary>
    /// Which discovered games the list shows. Not cosmetic on a Deck: the scan now returns installed
    /// Steam titles too, and a library of hundreds of them is unreachable with a thumbstick unless
    /// it can be narrowed. Mirrors the browser UI's filter chips, minus the store axis — Heroic's
    /// storefronts are a Desktop Mode concern and each extra pill costs a d-pad press here.
    /// </summary>
    private enum AddFilter { Suggested, All, Steam, Shortcut, Heroic, NoPath }
    private AddFilter _addFilter = AddFilter.Suggested;

    private static bool MatchesFilter(ScanCandidate c, AddFilter f) => f switch
    {
        AddFilter.Suggested => !c.HasSteamCloud,
        AddFilter.Steam => c.Source == ScanSource.SteamInstalled,
        AddFilter.Shortcut => c.Source == ScanSource.SteamShortcut,
        AddFilter.Heroic => c.Source == ScanSource.Heroic,
        AddFilter.NoPath => string.IsNullOrEmpty(c.SuggestedSaveDir),
        _ => true,
    };

    private static string FilterLabel(AddFilter f) => f switch
    {
        AddFilter.Suggested => "Suggested",
        AddFilter.Steam => "Steam",
        AddFilter.Shortcut => "Added to Steam",
        AddFilter.Heroic => "Heroic",
        AddFilter.NoPath => "Needs path",
        _ => "All",
    };

    // Set-folder (path browser) state. Listing is cached and only refreshed when the path changes,
    // so navigating does not re-walk the disk every frame.
    private int _folderTargetId = -1;
    private string _browsePath = "";
    private string? _lastListedPath = null;
    private BrowseListing? _listing;
    private string[] _files = Array.Empty<string>();
    private bool _focusFirstEntry;

    private string _copyResult = "";

    private readonly Vector2D<int> _size;

    // Screenshot-and-exit mode: render a few frames so the first-frame layout settles, then capture
    // and quit. Non-interactive, so it works over SSH to a Deck or in a scripted design loop.
    private readonly string? _screenshotPath;
    private int _framesRendered;
    private const int ScreenshotWarmupFrames = 5;
    // Hard ceiling so a scan that never returns cannot hang an unattended capture forever.
    private const int ScreenshotMaxFrames = 600;
    private bool _autoScan;
    private bool _pendingFolderScreen;
    private int _settleFrames;
    // Frames to keep rendering after all work finishes, so a screen change made DURING a frame is
    // actually drawn, and tweens land near their targets, before the framebuffer is read.
    private const int ScreenshotSettleFrames = 20;

    private UiApp(AgentConfig config, Vector2D<int> size, string? screenshotPath)
    {
        _config = config;
        _size = size;
        _screenshotPath = screenshotPath;
        _scanner = new LinuxGameScanner(new Detection(config));
        _browser = new PathBrowser(SteamRoots.BrowseRoots().Concat(HeroicRoots.BrowseRoots()));
        _launch = Daemon.LinuxLaunchCommand();
        _settings = new SettingsScreen(config, new SystemdAutoStart());
        _leaseWarnings = LeaseWarningStore.For(config);
    }

    public static int Run(AgentConfig config, string? sizeOverride = null, string? screenshotPath = null,
        bool gallery = false, string? startScreen = null, bool autoScan = false,
        string? navScript = null, bool navDebug = false, string? pointerAt = null)
    {
        NavDebug.Enabled = navDebug;
        _pointerPark = ParsePointer(pointerAt);

        // Printed before the window opens, so the probe result is readable even from a run that
        // cannot open a display — which is how a self-contained publish gets checked on a Deck.
        if (navDebug) Console.WriteLine("nav api: " + ImGuiInternal.Status);

        var app = new UiApp(config, ParseSize(sizeOverride), screenshotPath);
        if (gallery) app._screen = Screen.Gallery;
        else if (startScreen is not null)
        {
            var target = ParseScreen(startScreen);
            if (target == Screen.SetFolder) { app._screen = Screen.AddGame; app._pendingFolderScreen = true; }
            else app._screen = target;
        }
        app._autoScan = autoScan;
        foreach (var key in ParseNavScript(navScript)) app._navScript.Enqueue(key);
        return app.RunLoop();
    }

    /// <summary>
    /// Which screen to open on. A development affordance: paired with <c>--screenshot</c> it captures
    /// any screen unattended, so a layout change can be reviewed across the whole UI without a person
    /// driving it. Not something a user has any reason to pass.
    /// </summary>
    private static Screen ParseScreen(string name) => name.ToLowerInvariant() switch
    {
        "status" or "overview" => Screen.Status,
        "add" or "addgame" => Screen.AddGame,
        "folder" or "setfolder" => Screen.SetFolder,
        "launch" or "launchsetup" or "steam" => Screen.LaunchSetup,
        "settings" or "config" => Screen.Settings,
        "gallery" => Screen.Gallery,
        _ => Screen.Status,
    };

    /// <summary>Turn "right,down,a" into the nav keys a gamepad would have produced.</summary>
    private static IEnumerable<ImGuiKey> ParseNavScript(string? script)
    {
        if (string.IsNullOrWhiteSpace(script)) yield break;
        foreach (var raw in script.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim().ToLowerInvariant();
            var key = token switch
            {
                "up" or "u" => ImGuiKey.GamepadDpadUp,
                "down" or "d" => ImGuiKey.GamepadDpadDown,
                "left" or "l" => ImGuiKey.GamepadDpadLeft,
                "right" or "r" => ImGuiKey.GamepadDpadRight,
                "a" => ImGuiKey.GamepadFaceDown,
                "b" => ImGuiKey.GamepadFaceRight,
                _ => (ImGuiKey?)null,
            };
            if (key is null) Console.Error.WriteLine($"Ignoring unknown --nav step '{token}'.");
            else yield return key.Value;
        }
    }

    /// <summary>
    /// Window size, defaulting to the Deck's native 1280x800. The override exists so the same binary
    /// can be run windowed on a dev box (WSLg) at the exact panel size — testing the layout at any
    /// other size defeats the point. Source: <c>--size WxH</c>, else SAVELOCKER_UI_SIZE.
    /// </summary>
    private static Vector2D<int> ParseSize(string? raw)
    {
        var value = raw ?? Environment.GetEnvironmentVariable("SAVELOCKER_UI_SIZE");
        if (!string.IsNullOrWhiteSpace(value))
        {
            var parts = value.Split('x', 'X');
            if (parts.Length == 2 &&
                int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h) &&
                w >= 640 && h >= 400)
                return new Vector2D<int>(w, h);

            Console.Error.WriteLine($"Ignoring bad UI size '{value}' — expected WxH, e.g. 1280x800.");
        }
        return new Vector2D<int>(1280, 800);
    }

    private int RunLoop()
    {
        Window.PrioritizeSdl();

        var options = WindowOptions.Default with
        {
            Title = "SaveLocker",
            Size = _size,
            VSync = true,
            // 60 fps flat. The original 30 cap was a battery measure; it was relaxed once the
            // maintainer settled that this is a configuration surface used for minutes at a time,
            // not a game running for hours, with no 3D engine behind it. VSync is what actually
            // stops an unbounded spin, and it stays on.
            FramesPerSecond = 60,
            UpdatesPerSecond = 60,
        };

        _window = Window.Create(options);
        _window.Load += OnLoad;
        _window.FramebufferResize += size => _gl.Viewport(size);
        _window.Render += OnRender;
        _window.Closing += () =>
        {
            Sound.Shutdown();
            _controller?.Dispose();
            _input?.Dispose();
            _gl?.Dispose();
        };

        _window.Run();
        return 0;
    }

    private void OnLoad()
    {
        _gl = _window.CreateOpenGL();
        _input = _window.CreateInput();

        // Fonts must be added inside onConfigureIO: Silk invokes it before baking the font device
        // texture, so anything added afterwards is silently ignored.
        _controller = new ImGuiController(_gl, _window, _input, null, Theme.LoadFonts);
        Theme.ApplyStyle();
        Art.Load(_gl);

        // A scripted capture must never make noise — on a Deck it would fire into whatever the user
        // is actually listening to. It still opens the device, muted, so the Settings screen reports
        // the real audio state in a screenshot rather than claiming there is no device.
        Sound.Init(_config.UiSoundsMuted || _screenshotPath is not null);

        var io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.NavEnableGamepad;
        io.BackendFlags |= ImGuiBackendFlags.HasGamepad;

        // Silk only tracks gamepad button state once something subscribes to ButtonDown — without
        // this the per-frame Pressed poll reads a stale false and nav is dead. See Gotchas.md.
        foreach (var p in _input.Gamepads) HookPad(p);
        _input.ConnectionChanged += (device, connected) =>
        {
            if (connected && device is IGamepad gp) HookPad(gp);
        };

        // This UI has no text fields, but SDL leaves text input enabled by default and gamescope
        // pops the on-screen keyboard for any client with text input active. Turn it off so the OSK
        // does not cover the UI on launch.
        try { SdlApi.GetApi().StopTextInput(); } catch { /* best-effort */ }
    }

    private void HookPad(IGamepad pad)
    {
        if (!_hookedPads.Add(pad.Index)) return;
        pad.ButtonDown += (_, button) =>
        {
            var key = MapButton(button.Name);
            if (key is null) return;
            lock (_navLock)
            {
                _navQueue.Add(key.Value);
                if (IsDpad(key.Value))
                {
                    var now = Environment.TickCount64;
                    _heldSince[key.Value] = now;
                    _lastRepeat[key.Value] = now;
                }
            }
        };
        pad.ButtonUp += (_, button) =>
        {
            var key = MapButton(button.Name);
            if (key is null) return;
            lock (_navLock) { _heldSince.Remove(key.Value); _lastRepeat.Remove(key.Value); }
        };
    }

    private static bool IsDpad(ImGuiKey k) =>
        k is ImGuiKey.GamepadDpadUp or ImGuiKey.GamepadDpadDown
          or ImGuiKey.GamepadDpadLeft or ImGuiKey.GamepadDpadRight;

    private static ImGuiKey? MapButton(ButtonName name) => name switch
    {
        ButtonName.DPadUp => ImGuiKey.GamepadDpadUp,
        ButtonName.DPadDown => ImGuiKey.GamepadDpadDown,
        ButtonName.DPadLeft => ImGuiKey.GamepadDpadLeft,
        ButtonName.DPadRight => ImGuiKey.GamepadDpadRight,
        ButtonName.A => ImGuiKey.GamepadFaceDown,   // activate
        ButtonName.B => ImGuiKey.GamepadFaceRight,  // cancel
        _ => null,
    };

    private void OnRender(double delta)
    {
        // Feed one scripted press every few frames, through the same queue the pad writes to.
        // Nothing is injected until focus has settled: SetKeyboardFocusHere resolves at the end of
        // the first frame and overrides nav movement, so a press on frame 0 is simply eaten.
        // Wait for in-flight work before replaying: a real user presses Right after a scan has
        // finished, not into a spinner, and a script that races the scan tests nothing useful.
        var scriptReady = _framesRendered >= NavScriptStartFrame
                          && _scanTask is not { IsCompleted: false }
                          && _enrollTask is not { IsCompleted: false };
        if (_navScript.Count > 0 && scriptReady)
        {
            if (--_navScriptCooldown <= 0)
            {
                _navScriptCooldown = NavScriptFrameGap;
                lock (_navLock) _navQueue.Add(_navScript.Dequeue());
            }
        }

        NavDebug.BeginFrame();
        Widgets.BeginFrame();
        FeedImGuiNav();
        // Before Update, which is what calls NewFrame: a queued mouse position must be in the queue
        // NewFrame drains, because NewFrame is also where HoveredWindow is resolved. MouseDelta read
        // here is last frame's, which costs the highlight a frame nobody can perceive.
        TrackCursorOwner();

        // SDL's error string is sticky — it is never cleared on success, only overwritten on the
        // next failure. Silk's SdlContext.FramebufferSize calls SDL_GL_GetDrawableSize (which
        // returns void) and then decides whether it failed by reading SDL_GetError, so ANY stale
        // error left behind by an earlier harmless probe surfaces here as a fatal
        // "That operation is not supported" on the first frame. Clearing it each frame is what SDL
        // documents callers should do before a call whose result they test this way. Costs nothing.
        try { SdlApi.GetApi().ClearError(); } catch { /* best-effort */ }

        _controller.Update((float)delta);


        _gl.ClearColor(Theme.BgGlobal.X, Theme.BgGlobal.Y, Theme.BgGlobal.Z, 1.0f);
        _gl.Clear((uint)ClearBufferMask.ColorBufferBit);

        var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
                    ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse |
                    ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoScrollbar;

        ImGui.SetNextWindowPos(Vector2.Zero);
        ImGui.SetNextWindowSize(ImGui.GetIO().DisplaySize);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.Begin("SaveLocker", flags);
        ImGui.PopStyleVar();

        var size = ImGui.GetIO().DisplaySize;

        if (_screen == Screen.Gallery)
        {
            // The dev gallery takes the whole surface: it is taller than the shell's content area
            // and exists to show components, not to be navigated like a real screen.
            ImGui.SetCursorPos(new Vector2(Theme.Layout.Gutter, Theme.Layout.Gutter));
            ImGui.BeginChild("gallery",
                new Vector2(size.X - Theme.Layout.Gutter * 2, size.Y - Theme.Layout.Gutter * 2));
            Gallery.Draw();
            ImGui.EndChild();
        }
        else
        {
            DrawHeader(size);
            DrawRail(size);
            DrawContent(size);
            DrawHintBar(size);
            DrawSeparators(size);
        }

        // Drawn before the request is aged and before the crossing is resolved, so the overlay shows
        // the state that actually governed THIS frame's draw rather than next frame's.
        NavDebug.Draw(new NavDebug.Frame(
            _screen.ToString(), _focusZone.ToString(), _activeRailId, _bestContentId,
            _pendingCrossFrames));

        // Anything left armed had no enabled widget to land on this frame; let it age out.
        Widgets.AgeFocusRequest();

        RecoverStrandedCursor();
        ResolvePaneCrossing();

        ImGui.End();
        _controller.Render();

        // A mid-transition capture would be a dim, offset frame — the fade counts as work in
        // progress for screenshot purposes.
        var busy = _scanTask is { IsCompleted: false } || _enrollTask is { IsCompleted: false }
                   || _pendingFolderScreen || _screenFade < 1f || _navScript.Count > 0;
        _settleFrames = busy ? 0 : _settleFrames + 1;

        if (_screenshotPath is not null && ++_framesRendered >= ScreenshotWarmupFrames
            && (_settleFrames >= ScreenshotSettleFrames || _framesRendered >= ScreenshotMaxFrames))
        {
            int code = 0;
            try
            {
                var fb = _window.FramebufferSize;
                Screenshot.Capture(_gl, fb.X, fb.Y, _screenshotPath);
                Console.WriteLine($"Wrote {fb.X}x{fb.Y} screenshot to {_screenshotPath}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Screenshot failed: " + ex.Message);
                code = 1;
            }

            // Exit rather than Close(): we are inside the render callback, and Silk calls
            // SwapBuffers after it returns. Close() runs the Closing handler, which disposes the GL
            // context, so that SwapBuffers then throws "Context not created". The PNG stream is
            // already flushed and closed by this point, and this mode's entire job is done.
            Console.Out.Flush();
            Environment.Exit(code);
        }
    }

    /// <summary>
    /// Move the cursor between the rail and the content area.
    ///
    /// ImGui scores directional nav purely on geometry, and the two panes routinely share no
    /// vertical overlap — a rail entry near the top has nothing beside it when the content's first
    /// control sits far lower — so it simply refuses the move. That is what made the UI feel like
    /// it needed A to "enter" a pane and B to leave it.
    ///
    /// So both crossings are explicit and unconditional: the rail is one column, so Right out of it
    /// can never mean anything else, and Left out of the content always returns to the rail entry for
    /// the screen you are on. Each fires a focus request that <see cref="Widgets"/> serves through
    /// igSetFocusID on the very next frame.
    ///
    /// Left is NOT deferred to see whether ImGui would move the cursor itself first. It was, so that
    /// Left could step through a screen's own columns before leaving the pane — but no screen has
    /// side-by-side focusable columns (the two-column layouts are text against controls), and the
    /// deferral cost a frame on every press and left the request armed when it did not land.
    /// </summary>
    private void ResolvePaneCrossing()
    {
        // A cross that had nowhere to land completes as soon as the content has a control.
        if (_pendingCrossFrames > 0)
        {
            _pendingCrossFrames--;
            if (_bestContentId != 0)
            {
                _pendingCrossFrames = 0;
                EnterContent();
            }
        }

        if (_navRightFired && _focusZone == Zone.Rail)
        {
            if (_bestContentId != 0) EnterContent();
            else _pendingCrossFrames = PendingCrossFrames;   // scan still running
        }
        else if (_navLeftFired && _focusZone == Zone.Content)
        {
            // Back to the rail entry for the screen you are on — never the nearest one by pixels.
            _focusZone = Zone.Rail;
            Widgets.RequestFocus(_activeRailId);
        }

        // B has to be handled here now. It used to be ImGui's built-in NavCancel and nothing else,
        // which worked only by accident: with the cursor app-placed, RecoverStrandedCursor puts back
        // whatever NavCancel cleared, in the same frame. Owning it also fixes a hint bar that has
        // been lying — on Set save folder the bar promises "Cancel", but NavCancel only ever moved the
        // nav cursor and left the screen up; the on-screen Cancel button was the only way out.
        if (_navBackFired)
        {
            // In the folder browser B means "up a directory", not "cancel". Inside a file tree that is
            // what back reads as, and cancelling is already reachable two other ways (the Cancel
            // button, or Left out to the rail) — so spending B on a third exit wasted it.
            // At the roots there is nothing above to climb to, so back leaves the screen rather than
            // becoming a dead button.
            if (_screen == Screen.SetFolder)
            {
                bool canClimb = _listing is not null &&
                                (_listing.Parent is not null || !string.IsNullOrEmpty(_listing.Path));
                if (canClimb)
                {
                    _browsePath = _listing!.Parent ?? "";
                }
                else
                {
                    _screen = Screen.AddGame;
                    _bestContentId = 0;
                    _focusZone = Zone.Rail;
                    Widgets.RequestFocus(_activeRailId);
                }
            }
            else if (_focusZone == Zone.Content)
            {
                _focusZone = Zone.Rail;
                Widgets.RequestFocus(_activeRailId);
            }
        }

        _navLeftFired = _navRightFired = _navBackFired = false;
    }

    private void EnterContent()
    {
        _focusZone = Zone.Content;
        Widgets.RequestFocus(_bestContentId);
    }

    /// <summary>
    /// Put the cursor back on a real control if no submitted item claimed it this frame.
    ///
    /// A Deck has no pointer, so the focus ring IS the cursor: if ImGui leaves NavId on something no
    /// widget answers for, the screen shows a highlight with nothing selected inside it and the D-pad
    /// reads as dead. That is what the hint bar appearing "selectable, with no options in it"
    /// actually was — a pane-sized highlight whose bottom edge sits on the hint bar, reported from
    /// the Deck as a footer you could navigate to.
    ///
    /// This is an invariant rather than a patch for one cause: whatever strands the cursor — an item
    /// that stopped being submitted, a list that emptied, a screen whose ids all changed — the cursor
    /// belongs on a control, and there is now a primitive that can put it there.
    /// </summary>
    private void RecoverStrandedCursor()
    {
        if (Widgets.CursorOnRealItem || Widgets.FocusRequestPending)
        {
            _strandedFrames = 0;
            return;
        }

        _strandedFrames++;

        // Prefer where the cursor last legitimately was — but only once. If that item is gone rather
        // than merely unfocused (browsing into another folder retires every row id, so this is a real
        // path, not a theoretical one) then re-requesting it can never land, and retrying it forever
        // would leave the cursor stranded while looking busy. Second attempt goes to the pane anchor,
        // which is always a live id.
        var target = _strandedFrames == 1 && Widgets.LastGoodId != 0
            ? Widgets.LastGoodId
            : _focusZone == Zone.Rail ? _activeRailId : _bestContentId;

        Widgets.RequestFocus(target);
    }

    /// <summary>
    /// Whether both panes must stay navigable so a hand-off can complete.
    ///
    /// Only the fallback needs this. <c>SetKeyboardFocusHere</c> can only move a cursor that is not
    /// already inside a <c>NoNav</c> window, so suppressing the pane it is leaving strands it. That
    /// concession is also the bug: while both panes are live, ImGui's geometric scoring can carry a
    /// Down out of the content and onto "Quit". <c>igSetFocusID</c> needs no concession — it writes
    /// the cursor's new home directly — so with the internal API each pane is navigable only while it
    /// owns the cursor, which is what makes Up/Down unable to cross panes at all.
    /// </summary>
    private static bool BothPanesForHandoff =>
        !ImGuiInternal.Available && Widgets.FocusRequestPending;

    private bool Connected => !string.IsNullOrEmpty(_config.ApiKey);

    /// <summary>
    /// The top bar: mark, connection state, server. Mirrors the console's StatusHeader so the two
    /// surfaces are recognisably the same product.
    /// </summary>
    private void DrawHeader(Vector2 size)
    {
        ImGui.SetCursorPos(Vector2.Zero);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.BgCard);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding,
            new Vector2(Theme.Layout.Gutter, Theme.Space.Sm));
        ImGui.BeginChild("header", new Vector2(size.X, Theme.Layout.HeaderHeight),
            ImGuiChildFlags.AlwaysUseWindowPadding,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoNav);

        // Vertical centring is measured inside the child's content box, so the padding pushed above
        // is already accounted for — do not subtract it again.
        const float markH = 40f;
        if (Art.Logo.Ok)
        {
            var inner = Theme.Layout.HeaderHeight - Theme.Space.Sm * 2;
            ImGui.SetCursorPosY(Theme.Space.Sm + MathF.Max(0f, (inner - markH) / 2f));
            ImGui.Image(Art.Logo.Id, new Vector2(Art.Logo.WidthAt(markH), markH));
            ImGui.SameLine(0, Theme.Space.Md);
        }

        ImGui.BeginGroup();
        Widgets.EyebrowLabel("Agent Status");
        var accent = Connected ? Theme.AccentGreen : Theme.AccentAmber;
        Widgets.StatusDot(accent, 8f);
        ImGui.SameLine(0, Theme.Space.Sm);
        Widgets.Text(Connected ? "CONNECTED" : "NOT ENROLLED", accent, Theme.BodyStrong);
        ImGui.EndGroup();

        // Server chip, right-aligned.
        var url = _config.ServerUrl?.Replace("https://", "").Replace("http://", "") ?? "";
        if (!string.IsNullOrEmpty(url))
        {
            Theme.PushFont(Theme.Mono);
            var chipW = ImGui.CalcTextSize(url).X + 52f;
            Theme.PopFont(Theme.Mono);

            ImGui.SameLine();
            var pad = ImGui.GetContentRegionAvail().X - chipW;
            if (pad > 0) { ImGui.Dummy(new Vector2(pad, 0)); ImGui.SameLine(); }
            ImGui.SetCursorPosY((Theme.Layout.HeaderHeight - 26f) / 2f - Theme.Space.Sm);
            Widgets.Badge(url, Theme.AccentGreen, Icons.Server, mono: true);
        }

        ImGui.EndChild();
        ImGui.PopStyleVar();
        ImGui.PopStyleColor();
    }

    /// <summary>The left rail of top-level destinations. Set save folder is a sub-flow of Add game,
    /// so it is deliberately not a rail entry.</summary>
    private void DrawRail(Vector2 size)
    {
        var height = size.Y - Theme.Layout.HeaderHeight - Theme.Layout.HintBarHeight;

        ImGui.SetCursorPos(new Vector2(0, Theme.Layout.HeaderHeight));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.BgCard);
        // Horizontal padding is FocusClearance, not Space.Sm: rail entries are full-width, so the
        // padding is the only room their focus ring has.
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding,
            new Vector2(Theme.Layout.FocusClearance, Theme.Space.Md));
        // Flattened so the rail is never a nav *container* — that is what drew a highlight around
        // the whole pane and produced the A-to-enter / B-to-leave feel. Navigable only while the
        // cursor lives here: without that gate both panes share one nav space and a Down inside the
        // content lands on a rail entry, which is why Down from "Scan for games" jumped to
        // "Add game" and left the discovered list unreachable.
        var railFlags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NavFlattened;
        if (_focusZone != Zone.Rail && !BothPanesForHandoff)
            railFlags |= ImGuiWindowFlags.NoNav;
        ImGui.BeginChild("rail", new Vector2(Theme.Layout.RailWidth, height),
            ImGuiChildFlags.AlwaysUseWindowPadding, railFlags);
        NavDebug.PushScope("rail");

        var items = new (string Label, Icons.Glyph Icon, Screen Target, bool Active)[]
        {
            ("Overview", Icons.Monitor, Screen.Status, _screen == Screen.Status),
            ("Add game", Icons.Plus, Screen.AddGame, _screen is Screen.AddGame or Screen.SetFolder),
            ("Steam setup", Icons.HardDrive, Screen.LaunchSetup, _screen == Screen.LaunchSetup),
            ("Settings", Icons.Settings, Screen.Settings, _screen == Screen.Settings),
        };

        foreach (var (label, icon, target, active) in items)
        {
            // Land the cursor on the rail entry for the screen already being shown. Without an
            // initial nav target ImGui starts with nothing focused, so the first D-pad press only
            // picks a starting item and appears to do nothing.
            //
            // ⚠️ This is suspected of not working and is kept only because that is unproven. The rail
            // is a NavFlattened child and SetKeyboardFocusHere is a tabbing request confined to the
            // active focus scope (ocornut/imgui#7226) — the restriction ImGuiInternal exists to get
            // around — and it resolves at the end of frame 0, on top of the request
            // RecoverStrandedCursor places the same frame. If "A does nothing at open" survives the
            // hover fix on hardware, delete this block first: RecoverStrandedCursor already seeds
            // frame 0 through igSetFocusID, which is why removing it changed nothing under WSLg.
            if (_focusRailOnce && active)
            {
                _focusRailOnce = false;
                ImGui.SetKeyboardFocusHere();
            }
            if (Widgets.RailItem(label, icon, active)) Go(target);
            if (active) _activeRailId = Widgets.LastRailItemId;
        }

        // Quit sits at the bottom, away from the destinations — it is not a place you go.
        var used = ImGui.GetCursorPosY();
        var spare = height - used - (ImGui.GetTextLineHeight() + Theme.Space.Md * 2) - Theme.Space.Md;
        if (spare > 0) ImGui.Dummy(new Vector2(0, spare));

        if (Widgets.RailItem("Quit", Icons.X, false)) _window.Close();

        NavDebug.PopScope();
        ImGui.EndChild();
        NavDebug.NoteContainer("rail");
        ImGui.PopStyleVar();
        ImGui.PopStyleColor();
    }

    /// <summary>
    /// The three shell separators, drawn on the FOREGROUND list after every pane is submitted.
    ///
    /// They used to be drawn on the root window's draw list as each pane was built, which put them
    /// UNDER the panes: a child window's draw list merges after its parent's, and the rail and content
    /// backgrounds both start at exactly y = HeaderHeight, so they painted over the header rule. It
    /// survived only in the 1 px gutter at x = RailWidth — the "remnants at the tops of the vertical
    /// lines" reported from the Deck. Drawing them last is what makes them continuous.
    /// </summary>
    private static void DrawSeparators(Vector2 size)
    {
        var dl = ImGui.GetForegroundDrawList();
        var colour = ImGui.ColorConvertFloat4ToU32(Theme.Border);
        var railBottom = size.Y - Theme.Layout.HintBarHeight;

        dl.AddLine(new Vector2(0, Theme.Layout.HeaderHeight),
                   new Vector2(size.X, Theme.Layout.HeaderHeight), colour, 1f);
        dl.AddLine(new Vector2(Theme.Layout.RailWidth, Theme.Layout.HeaderHeight),
                   new Vector2(Theme.Layout.RailWidth, railBottom), colour, 1f);
        dl.AddLine(new Vector2(0, railBottom), new Vector2(size.X, railBottom), colour, 1f);
    }

    private void DrawContent(Vector2 size)
    {
        var height = size.Y - Theme.Layout.HeaderHeight - Theme.Layout.HintBarHeight;

        ImGui.SetCursorPos(new Vector2(Theme.Layout.RailWidth + 1f, Theme.Layout.HeaderHeight));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.BgGlobal);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding,
            new Vector2(Theme.Layout.Gutter, Theme.Layout.Gutter));
        var contentFlags = ImGuiWindowFlags.NavFlattened;
        if (_focusZone != Zone.Content && !BothPanesForHandoff)
            contentFlags |= ImGuiWindowFlags.NoNav;
        ImGui.BeginChild("content",
            new Vector2(size.X - Theme.Layout.RailWidth - 1f, height),
            ImGuiChildFlags.AlwaysUseWindowPadding, contentFlags);
        NavDebug.PushScope("content");

        // Cross-fade on navigation. Without it a rail press swaps the entire right-hand two-thirds
        // of the screen between two frames, which reads as a glitch rather than a transition.
        // Ramp is time-based, not per-frame, so it is identical whatever the loop is doing.
        if (_renderedScreen != _screen)
        {
            _renderedScreen = _screen;
            _screenFade = 0f;
        }
        _screenFade = MathF.Min(1f, _screenFade + ImGui.GetIO().DeltaTime / ScreenFadeSeconds);

        // Ease-out cubic: fast to mostly-visible, then settles. A linear fade reads as sluggish
        // because the eye spends most of it watching the dim half.
        var eased = 1f - MathF.Pow(1f - _screenFade, 3f);

        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, eased);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (1f - eased) * 12f);

        // Collect this screen's focusable controls so Right can land on the visually topmost one
        // rather than whichever happened to be drawn first.
        Widgets.BeginFocusScan();

        switch (_screen)
        {
            case Screen.Status: DrawStatus(); break;
            case Screen.AddGame: DrawAddGame(); break;
            case Screen.SetFolder: DrawSetFolder(); break;
            case Screen.LaunchSetup: DrawLaunchSetup(); break;
            case Screen.Settings: _settings.Draw(); break;
        }

        var best = Widgets.EndFocusScan();
        if (best != 0) _bestContentId = best;

        ImGui.PopStyleVar();
        NavDebug.PopScope();
        ImGui.EndChild();
        NavDebug.NoteContainer("content");
        ImGui.PopStyleVar();
        ImGui.PopStyleColor();
    }

    /// <summary>
    /// The gamepad hint bar. The React UI has no equivalent because a desktop has a pointer; on a
    /// Deck this is the only thing telling the user what the face buttons do.
    /// </summary>
    private void DrawHintBar(Vector2 size)
    {
        var y = size.Y - Theme.Layout.HintBarHeight;

        ImGui.SetCursorPos(new Vector2(0, y));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.BgCard);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding,
            new Vector2(Theme.Layout.Gutter, Theme.Space.Sm + 2f));
        ImGui.BeginChild("hints", new Vector2(size.X, Theme.Layout.HintBarHeight),
            ImGuiChildFlags.AlwaysUseWindowPadding,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoNav);

        Widgets.GamepadHint("A", "Select");
        ImGui.SameLine(0, Theme.Space.Lg);
        Widgets.GamepadHint("B", _screen == Screen.SetFolder ? "Cancel" : "Back");
        ImGui.SameLine(0, Theme.Space.Lg);
        Widgets.GamepadHintIcon(Icons.Dpad, "Move");

        var version = $"SaveLocker {BuildVersion}";
        Theme.PushFont(Theme.Caption);
        var vw = ImGui.CalcTextSize(version).X;
        Theme.PopFont(Theme.Caption);

        ImGui.SameLine();
        var pad = ImGui.GetContentRegionAvail().X - vw;
        if (pad > 0) { ImGui.Dummy(new Vector2(pad, 0)); ImGui.SameLine(); }
        ImGui.AlignTextToFramePadding();
        Widgets.Text(version, Theme.TextDim, Theme.Caption);

        ImGui.EndChild();
        ImGui.PopStyleVar();
        ImGui.PopStyleColor();
    }

    private static string BuildVersion =>
        System.Reflection.CustomAttributeExtensions
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(
                System.Reflection.Assembly.GetExecutingAssembly())
            ?.InformationalVersion.Split('+')[0] ?? "dev";

    private void Go(Screen target)
    {
        if (_screen == target) return;
        _screen = target;
        _copyResult = "";
        // The new screen's content has not been measured yet, and the cursor stays on the rail
        // entry the user just activated.
        _focusZone = Zone.Rail;
        _bestContentId = 0;
        _pendingCrossFrames = 0;
    }

    private void DrawStatus()
    {
        DrawLeaseWarnings();

        if (!Connected)
        {
            Widgets.Banner("notenrolled", "This device is not enrolled",
                "Run  savelocker enroll --file <policy.json>  once in Desktop Mode, then come back here.",
                Theme.AccentAmber, Icons.AlertTriangle);
            Widgets.Gap(Theme.Space.Md);
        }

        // Stat tiles: the console's headline element, and what fills the top of the panel.
        var avail = ImGui.GetContentRegionAvail().X;
        var tileW = (avail - Theme.Space.Md * 2) / 3f;
        var lastSync = _config.LastSyncTime.HasValue
            ? FormatAgo(DateTime.UtcNow - _config.LastSyncTime.Value)
            : "-";

        Widgets.StatTile(_config.Games.Count.ToString(), "Games Tracked", Theme.AccentGreen, tileW, 112f);
        ImGui.SameLine(0, Theme.Space.Md);
        Widgets.StatTile(_config.TotalSavesPushed.ToString(), "Saves Backed Up", Theme.TextPrimary, tileW, 112f);
        ImGui.SameLine(0, Theme.Space.Md);
        Widgets.StatTile(lastSync, "Last Sync", Theme.TextMuted, tileW, 112f);

        Widgets.Gap(Theme.Space.Lg);

        Widgets.TwoColumn("status", 0.58f,
            left: () =>
            {
                Widgets.SectionHeader("Tracked games");
                if (_config.Games.Count == 0)
                {
                    Widgets.Text("No games tracked yet.", Theme.TextMuted);
                    Widgets.Gap(Theme.Space.Md);
                    if (Widgets.PillButton("Add a game", Widgets.ButtonKind.Primary, Icons.Plus))
                        Go(Screen.AddGame);
                }
                else
                {
                    foreach (var g in _config.Games)
                    {
                        var missing = string.IsNullOrEmpty(g.SaveDirectory);
                        Widgets.ListRow($"g{g.Name}", g.Name,
                            missing ? "No save folder set" : g.SaveDirectory,
                            missing ? Icons.AlertTriangle : Icons.Folder,
                            trailing: missing ? "needs setup" : null,
                            trailingColour: missing ? Theme.AccentAmber : null,
                            chevron: false);
                    }
                    Widgets.Gap(Theme.Space.Md);
                    if (Widgets.PillButton("Add a game", Widgets.ButtonKind.Primary, Icons.Plus))
                        Go(Screen.AddGame);
                }
            },
            right: () =>
            {
                Widgets.SectionHeader("This device");
                InfoRow("Machine", _config.MachineName ?? "-");
                InfoRow("Server", _config.ServerUrl ?? "-", mono: true);
                InfoRow("Agent", BuildVersion, mono: true);
                InfoRow("Status", Connected ? "Enrolled" : "Not enrolled",
                    colour: Connected ? Theme.AccentGreen : Theme.AccentAmber);

                Widgets.Gap(Theme.Space.Lg);
                Widgets.SectionHeader("Next step");

                // A staged update outranks the launch-option reminder: it is the only thing on this
                // screen that says the agent is about to change, and Game Mode has nowhere else to
                // say it.
                //
                // There is still no button, deliberately — this screen cannot restart the unit it is
                // being read from. What it CAN do is stop being a dead end. The old wording said the
                // update lands "the next time this device starts SaveLocker" and then "Nothing to
                // do", which is true, opaque and reads as "you cannot do anything": nothing on
                // screen says that phrase means a systemd --user unit, so someone who wants it now
                // has to guess. Updater.ApplyInstruction() answers it for this device instead.
                if (Updater.PendingVersion(_config) is { } pending)
                {
                    Widgets.TextWrapped(
                        $"SaveLocker {pending} is downloaded and ready. "
                        + Updater.ApplyInstruction(),
                        Theme.AccentGreen);
                    Widgets.Gap(Theme.Space.Sm);
                }

                Widgets.TextWrapped(
                    "Each game needs the SaveLocker launch option set once in Steam before it syncs.",
                    Theme.TextMuted);
                Widgets.Gap(Theme.Space.Sm);
                if (Widgets.PillButton("Steam setup", Widgets.ButtonKind.Secondary, Icons.Settings))
                    Go(Screen.LaunchSetup);
            });
    }

    /// <summary>
    /// Conflict-risk banners, read from the shared store. Dismissal writes through so the same
    /// warning does not reappear in the browser UI afterwards.
    /// </summary>
    private void DrawLeaseWarnings()
    {
        var now = Environment.TickCount64;
        if (now - _warningsReadAt > WarningsPollMs || _warningsReadAt == 0)
        {
            _warnings = _leaseWarnings.Read();
            _warningsReadAt = now;
        }

        if (_warnings.Count == 0) return;

        foreach (var w in _warnings)
        {
            if (Widgets.Banner($"lease{w.GameName}", $"Save conflict risk - {w.GameName}",
                    $"{w.HolderMachine} already has this game checked out. You launched without "
                    + "pulling their latest save, so a conflict will likely appear in the console "
                    + "when you exit.",
                    Theme.AccentAmber, Icons.AlertTriangle, dismissible: true))
            {
                _leaseWarnings.Clear(w.GameName);
                _warningsReadAt = 0;   // force a re-read next frame
            }
            Widgets.Gap(Theme.Space.Sm);
        }
        Widgets.Gap(Theme.Space.Sm);
    }

    /// <summary>A label/value pair, aligned into a column so a stack of them reads as a table.</summary>
    private static void InfoRow(string label, string value, bool mono = false, Vector4? colour = null)
    {
        Widgets.Text(label, Theme.TextMuted, Theme.Caption);
        ImGui.SameLine(96f);
        Widgets.Text(value, colour ?? Theme.TextPrimary, mono ? Theme.Mono : Theme.Body);
    }

    private void DrawAddGame()
    {
        if (!Connected)
        {
            Widgets.Banner("addblocked", "This device is not enrolled",
                "Games cannot be added yet. Run  savelocker enroll --file <policy.json>  once in "
                + "Desktop Mode, then return here.",
                Theme.AccentAmber, Icons.AlertTriangle);
            return;
        }

        // Dev affordance: start the scan without a button press, so an unattended capture can show
        // the populated candidate list rather than the empty state.
        if (_autoScan && _scanTask is null && _candidates.Count == 0)
        {
            _autoScan = false;
            _scanTask = _scanner.ScanAsync();
        }

        bool scanning = _scanTask is { IsCompleted: false };
        if (scanning)
        {
            var dl = ImGui.GetWindowDrawList();
            Icons.Spinner(dl, ImGui.GetCursorScreenPos(), 22f, Theme.AccentGreen, 2.5f);
            ImGui.Dummy(new Vector2(22, 22));
            ImGui.SameLine(0, Theme.Space.Md);
            ImGui.AlignTextToFramePadding();
            Widgets.Text("Scanning for games...", Theme.TextMuted);
        }
        else if (Widgets.PillButton("Scan for games", Widgets.ButtonKind.Primary, Icons.Search))
        {
            _scanTask = _scanner.ScanAsync();
        }

        // Fold a finished scan into the mutable candidate list.
        if (_scanTask is { IsCompletedSuccessfully: true } done)
        {
            _candidates.Clear();
            _candidates.AddRange(done.Result);
            _selected.Clear();
            _addStatus = $"Found {_candidates.Count} game(s).";
            _scanTask = null;

            // Dev affordance: `--screen folder` has nothing to browse until a scan has produced a
            // candidate, so entering that screen is deferred until one exists.
            if (_pendingFolderScreen && _candidates.Count > 0)
            {
                _pendingFolderScreen = false;
                EnterSetFolder(0);
            }
        }
        else if (_scanTask is { IsFaulted: true } faulted)
        {
            _addStatus = "Scan failed: " + (faulted.Exception?.GetBaseException().Message ?? "unknown error");
            _scanTask = null;
        }

        // Absorb a finished enroll.
        if (_enrollTask is { IsCompleted: true })
        {
            if (_enrollTask.IsCompletedSuccessfully)
            {
                var (enrolled, skipped) = _enrollTask.Result;
                _addStatus = $"Enrolled {enrolled}, skipped {skipped}.";
                _selected.Clear();
                _candidates.Clear();
                _screen = Screen.LaunchSetup;
            }
            else
            {
                _addStatus = "Enroll failed: " + (_enrollTask.Exception?.GetBaseException().Message ?? "unknown error");
            }
            _enrollTask = null;
        }

        if (!string.IsNullOrEmpty(_addStatus))
        {
            ImGui.SameLine(0, Theme.Space.Md);
            ImGui.AlignTextToFramePadding();
            Widgets.Text(_addStatus, Theme.TextMuted, Theme.Caption);
        }

        Widgets.Gap(Theme.Space.Md);

        // Names of ticked candidates still missing a save folder — enrollment is gated on them
        // (Phase 1.3): a game cannot be enrolled into the broken state that produced the Deck failures.
        var missing = _selected
            .Where(i => i >= 0 && i < _candidates.Count && string.IsNullOrEmpty(_candidates[i].SuggestedSaveDir))
            .Select(i => _candidates[i].Name)
            .ToList();

        // Only filters that would show something are offered: an empty "Heroic" pill on a Deck with
        // no Heroic install is one more thing to nav past that can never do anything. Suggested and
        // All always render, so the row never disappears.
        var filters = Enum.GetValues<AddFilter>()
            .Select(f => (Filter: f, Count: _candidates.Count(c => MatchesFilter(c, f))))
            .Where(x => x.Count > 0 || x.Filter is AddFilter.Suggested or AddFilter.All)
            .ToList();

        if (_candidates.Count > 0)
        {
            foreach (var (f, count) in filters)
            {
                if (f != filters[0].Filter) ImGui.SameLine(0, Theme.Space.Sm);
                var kind = _addFilter == f ? Widgets.ButtonKind.Primary : Widgets.ButtonKind.Ghost;
                if (Widgets.PillButton($"{FilterLabel(f)}  {count}", kind))
                    _addFilter = f;
            }
            Widgets.Gap(Theme.Space.Sm);
        }

        var shown = Enumerable.Range(0, _candidates.Count)
            .Where(i => MatchesFilter(_candidates[i], _addFilter))
            .ToList();

        // The section header is drawn BEFORE the list height is measured: measuring first and then
        // adding a header underflows the child by exactly the header's height, which clipped the
        // action bar off the bottom of the screen.
        Widgets.SectionHeader(_candidates.Count > 0
            ? $"Discovered games ({shown.Count} of {_candidates.Count})"
            : "Discovered games");

        // The action bar is pinned to the bottom so it never scrolls out of reach on a long list —
        // on a gamepad, hunting for a submit button below the fold is the worst case. The reserve
        // covers the button, the gap above it, and ImGui's item spacing on both.
        var buttonH = ImGui.GetTextLineHeight() + (Theme.Space.Sm + 2f) * 2;
        var barH = buttonH + Theme.Space.Sm + ImGui.GetStyle().ItemSpacing.Y * 2 + Theme.Space.Sm;
        var listH = MathF.Max(120f, ImGui.GetContentRegionAvail().Y - barH);

        // AlwaysUseWindowPadding: a child without it gets ZERO padding, so these full-width rows would
        // touch both edges and have their focus rings clipped (Theme.Layout.FocusClearance).
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding,
            new Vector2(Theme.Layout.FocusClearance, Theme.Layout.FocusClearance));
        ImGui.BeginChild("candidates", new Vector2(0, listH),
            ImGuiChildFlags.AlwaysUseWindowPadding, ImGuiWindowFlags.NavFlattened);
        NavDebug.PushScope("candidates");
        if (shown.Count > 0)
        {
            // Absolute indices throughout: _selected and Enroller both index into _candidates, so a
            // filter must never renumber anything — hiding a row would otherwise silently enroll a
            // different game than the one that was ticked.
            foreach (var i in shown) DrawCandidateRow(i);
        }
        else if (_candidates.Count > 0)
        {
            Widgets.Text($"No games match “{FilterLabel(_addFilter)}”. Choose All to see every one.",
                Theme.TextMuted);
        }
        else
        {
            Widgets.Text("Scan to find games with Proton prefixes on this device.", Theme.TextMuted);
        }
        NavDebug.PopScope();
        ImGui.EndChild();
        NavDebug.NoteContainer("candidates");
        ImGui.PopStyleVar();

        Widgets.Gap(Theme.Space.Sm);

        bool blocked = _selected.Count == 0 || missing.Count > 0 || _enrollTask is { IsCompleted: false };
        var label = _selected.Count > 0 ? $"Enroll {_selected.Count} selected" : "Enroll selected";
        if (Widgets.PillButton(label, Widgets.ButtonKind.Primary, Icons.Check, enabled: !blocked))
            _enrollTask = Enroller.EnrollAsync(_config, new List<ScanCandidate>(_candidates), _selected.ToArray());

        if (missing.Count > 0)
        {
            ImGui.SameLine(0, Theme.Space.Md);
            ImGui.AlignTextToFramePadding();
            Widgets.Text("Set a save folder for: " + string.Join(", ", missing),
                Theme.AccentAmberLt, Theme.Caption);
        }
    }

    private void DrawCandidateRow(int i)
    {
        var c = _candidates[i];
        bool alreadyTracked = _config.FindGame(c.Name) is not null;
        bool hasFolder = !string.IsNullOrEmpty(c.SuggestedSaveDir);
        // Steam has this game under more than one AppID: the caption names the one this scan follows.
        var duplicates = c.DuplicateShortcuts is { Count: > 0 } d ? d : null;

        ImGui.PushID(i);
        bool ticked = _selected.Contains(i);

        ImGui.BeginGroup();
        if (Widgets.CheckRow("sel", ref ticked, enabled: !alreadyTracked))
        {
            if (ticked) _selected.Add(i); else _selected.Remove(i);
        }
        ImGui.SameLine(0, Theme.Space.Md);

        ImGui.BeginGroup();
        Widgets.Text(c.Name, alreadyTracked ? Theme.TextMuted : Theme.TextPrimary, Theme.BodyStrong);
        ImGui.SameLine(0, Theme.Space.Sm);
        ImGui.AlignTextToFramePadding();
        Widgets.Text($"[{c.Source}]", Theme.TextDim, Theme.Caption);

        if (alreadyTracked)
        {
            ImGui.SameLine(0, Theme.Space.Sm);
            Widgets.Badge("already tracked", Theme.AccentGreen, Icons.Check);
        }
        else if (!hasFolder)
        {
            ImGui.SameLine(0, Theme.Space.Sm);
            Widgets.Badge("no save folder", Theme.AccentAmber, Icons.AlertTriangle);
        }
        else if (duplicates is not null)
        {
            ImGui.SameLine(0, Theme.Space.Sm);
            Widgets.Badge($"{duplicates.Count} AppIDs", Theme.AccentAmber, Icons.AlertTriangle);
        }

        if (!alreadyTracked)
        {
            Widgets.Text(hasFolder ? c.SuggestedSaveDir! : "Pick where this game keeps its saves.",
                hasFolder ? Theme.TextMuted : Theme.AccentAmberLt, Theme.Caption);
            if (Widgets.PillButton(hasFolder ? "Change folder" : "Set save folder",
                    hasFolder ? Widgets.ButtonKind.Ghost : Widgets.ButtonKind.Secondary, Icons.Folder))
                EnterSetFolder(i);
            if (duplicates is not null)
            {
                var tracked = duplicates.FirstOrDefault(x => x.Tracked);
                Widgets.TextWrapped(
                    tracked is null
                        ? $"Steam has {duplicates.Count} AppIDs for this game, but this scan resolved its save folder another way; none of them is followed."
                        : $"Steam has {duplicates.Count} AppIDs for this game; this scan follows AppID {tracked.AppId ?? "none"}. " +
                          "Its saves sync only if that is the one Steam launches.",
                    Theme.AccentAmberLt, Theme.Caption);
            }
        }
        ImGui.EndGroup();
        ImGui.EndGroup();

        Widgets.Gap(Theme.Space.Sm);
        var dl = ImGui.GetWindowDrawList();
        var p = ImGui.GetCursorScreenPos();
        dl.AddLine(p, p + new Vector2(ImGui.GetContentRegionAvail().X, 0),
            ImGui.ColorConvertFloat4ToU32(Theme.BgRowSep), 1f);
        Widgets.Gap(Theme.Space.Sm);

        ImGui.PopID();
    }

    private void EnterSetFolder(int candidateId)
    {
        _folderTargetId = candidateId;
        var c = _candidates[candidateId];
        // Seed the browser inside the game's own prefix so a Deck user never hunts the Wine tree:
        // existing save guess, then the deepest existing dir under the prefix, then the roots.
        _browsePath = (!string.IsNullOrEmpty(c.SuggestedSaveDir) && Directory.Exists(c.SuggestedSaveDir))
            ? c.SuggestedSaveDir!
            : SaveLocker.Shared.WinePrefix.BrowseStart(c.PrefixPath) ?? "";
        _lastListedPath = null;
        _screen = Screen.SetFolder;
    }

    private void DrawSetFolder()
    {
        if (_folderTargetId < 0 || _folderTargetId >= _candidates.Count)
        {
            _screen = Screen.AddGame;
            return;
        }

        var c = _candidates[_folderTargetId];
        Widgets.Text($"Save folder for {c.Name}", Theme.TextPrimary, Theme.Title);
        if (!string.IsNullOrEmpty(c.PrefixPath))
            Widgets.TextWrapped(
                "Opened inside this game's Proton prefix. Saves usually sit under Documents, "
                + "AppData/Roaming or AppData/LocalLow. The right pane lists files in the current "
                + "folder so you can confirm the save is really here.",
                Theme.TextMuted);
        Widgets.Gap(Theme.Space.Sm);

        if (_lastListedPath != _browsePath)
        {
            _listing = _browser.List(string.IsNullOrEmpty(_browsePath) ? null : _browsePath);
            _files = (_listing is null ? null : _browser.ListFiles(_browsePath)) ?? Array.Empty<string>();
            _lastListedPath = _browsePath;
            _focusFirstEntry = true; // re-seed the cursor onto the first row of the new folder
        }

        if (_listing is null)
        {
            Widgets.Banner("unreadable", "Folder not available",
                "That folder is not readable, or is outside the browsable roots.",
                Theme.AccentAmber, Icons.AlertTriangle);
            _browsePath = "";
            return;
        }

        Widgets.Badge(string.IsNullOrEmpty(_listing.Path) ? "(roots)" : _listing.Path,
            Theme.TextMuted, Icons.HardDrive, mono: true);
        Widgets.Gap(Theme.Space.Sm);

        var avail = ImGui.GetContentRegionAvail();
        var barH = ImGui.GetTextLineHeight() + Theme.Space.Sm * 2 + Theme.Space.Lg;
        float paneH = MathF.Max(200f, avail.Y - barH);

        // Left: folders, gamepad-navigable. Right: files here, read-only — the confirmation that
        // this is actually the save directory and not an empty parent.
        Widgets.TwoColumn("browse", 0.58f,
            left: () =>
            {
                bool wantFocus = _focusFirstEntry;
                _focusFirstEntry = false;

                if (_listing.Parent is not null || !string.IsNullOrEmpty(_listing.Path))
                {
                    if (wantFocus) { ImGui.SetKeyboardFocusHere(); wantFocus = false; }
                    if (Widgets.ListRow("up", "..", "Up one level", Icons.ChevronUp, chevron: false))
                        _browsePath = _listing.Parent ?? "";
                }
                foreach (var entry in _listing.Entries)
                {
                    if (wantFocus) { ImGui.SetKeyboardFocusHere(); wantFocus = false; }
                    if (Widgets.ListRow($"d{entry.Path}", entry.Name, null, Icons.Folder))
                        _browsePath = entry.Path;
                }
            },
            right: () =>
            {
                Widgets.SectionHeader(_files.Length == 0 ? "Files here (none)" : $"Files here ({_files.Length})");
                if (_files.Length == 0)
                    Widgets.TextWrapped("This folder has no files directly in it.", Theme.TextDim);
                foreach (var f in _files)
                    Widgets.Text(f, Theme.TextMuted, Theme.Caption);
            },
            height: paneH);

        Widgets.Gap(Theme.Space.Sm);

        bool canUse = !string.IsNullOrEmpty(_listing.Path);
        if (Widgets.PillButton("Use this folder", Widgets.ButtonKind.Primary, Icons.Check, enabled: canUse))
        {
            _candidates[_folderTargetId] = c with { SuggestedSaveDir = _listing.Path };
            _selected.Add(_folderTargetId);
            _screen = Screen.AddGame;
        }
        ImGui.SameLine(0, Theme.Space.Sm);
        if (Widgets.PillButton("Cancel", Widgets.ButtonKind.Ghost)) _screen = Screen.AddGame;
    }

    private void DrawLaunchSetup()
    {
        if (_launch.Command is null)
        {
            Widgets.Banner("nolaunch", "Not available here",
                _launch.Note ?? "No launch command available on this platform.",
                Theme.AccentAmber, Icons.AlertTriangle);
            return;
        }

        Widgets.Text("Steam launch setup", Theme.TextPrimary, Theme.Title);
        Widgets.TextWrapped("Add this to each game's Steam launch options so SaveLocker syncs it.",
            Theme.TextMuted);
        Widgets.Gap(Theme.Space.Md);

        // The command block: mono, accented, on a card. If the clipboard fails this is what the
        // user retypes, so it has to be unambiguous — hence the monospace face.
        Widgets.BeginCard("cmd", new Vector2(0, 0), Theme.BgCard, Theme.ChipBorder);
        Widgets.TextWrapped(_launch.Command, Theme.AccentGreen, Theme.Mono);
        Widgets.EndCard();

        Widgets.Gap(Theme.Space.Md);

        if (Widgets.PillButton("Copy to clipboard", Widgets.ButtonKind.Primary, Icons.Copy))
            _copyResult = SetClipboard(_launch.Command)
                ? "Copied."
                : "Copy failed - use the command above.";

        if (!string.IsNullOrEmpty(_copyResult))
        {
            ImGui.SameLine(0, Theme.Space.Md);
            ImGui.AlignTextToFramePadding();
            Widgets.Text(_copyResult,
                _copyResult.StartsWith("Copied") ? Theme.AccentGreen : Theme.AccentAmber,
                Theme.Caption);
        }

        Widgets.Gap(Theme.Space.Lg);
        Widgets.SectionHeader("Notes");

        Note("The command is identical for every game.",
            "Once one game is set up you can paste the same string into any other game's Launch "
            + "Options - SaveLocker is only needed here for the first game on a device.");
        Note("Use the full path shown above.",
            "Game Mode does not put ~/.local/bin on PATH.");
        Note("Tick \"Force the use of a specific Steam Play compatibility tool\".",
            "On a non-Steam shortcut, without this Proton never creates a prefix.");
    }

    private static void Note(string title, string body)
    {
        var dl = ImGui.GetWindowDrawList();
        var p = ImGui.GetCursorScreenPos();
        var lineH = ImGui.GetTextLineHeight();
        dl.AddCircleFilled(p + new Vector2(4f, lineH / 2f), 3f,
            ImGui.ColorConvertFloat4ToU32(Theme.AccentGreen), 10);

        ImGui.Indent(Theme.Space.Lg);
        Widgets.Text(title, Theme.TextPrimary, Theme.BodyStrong);
        Widgets.TextWrapped(body, Theme.TextMuted, Theme.Caption);
        ImGui.Unindent(Theme.Space.Lg);
        Widgets.Gap(Theme.Space.Sm);
    }

    /// <summary>
    /// Replay queued gamepad presses as single-frame ImGui nav pulses. Silk's ImGuiController wires
    /// keyboard and mouse only, so this glue is what makes D-pad + A/B drive focus (§3.3.2). Analog
    /// nav is suppressed so a resting trackpad-joystick cannot nudge focus.
    /// </summary>
    private void FeedImGuiNav()
    {
        var io = ImGui.GetIO();

        foreach (var k in new[]
                 {
                     ImGuiKey.GamepadLStickUp, ImGuiKey.GamepadLStickDown,
                     ImGuiKey.GamepadLStickLeft, ImGuiKey.GamepadLStickRight,
                 })
            io.AddKeyAnalogEvent(k, false, 0f);

        ImGuiKey[] fire;
        lock (_navLock)
        {
            // Held-to-repeat: a D-pad direction still down past the delay emits a pulse every tick,
            // so a long folder list scrolls without tapping dozens of times.
            var now = Environment.TickCount64;
            foreach (var (key, since) in _heldSince)
            {
                if (now - since < RepeatDelayMs) continue;
                if (now - _lastRepeat[key] < RepeatRateMs) continue;
                _navQueue.Add(key);
                _lastRepeat[key] = now;
            }

            fire = _navQueue.Distinct().ToArray();
            _navQueue.Clear();
            _navLeftFired = fire.Contains(ImGuiKey.GamepadDpadLeft);
            _navRightFired = fire.Contains(ImGuiKey.GamepadDpadRight);
            _navBackFired = fire.Contains(ImGuiKey.GamepadFaceRight);
        }

        // Release last frame's pulses, then press this frame's — a clean down/up per physical press.
        // Releasing unconditionally means a key that fires two frames running still reads as two
        // distinct presses (release+press queue as separate events ImGui processes in order).
        foreach (var k in _navDownLastFrame)
            io.AddKeyEvent(k, false);
        foreach (var k in fire)
            io.AddKeyEvent(k, true);
        _navDownLastFrame = fire;

        // A D-pad press takes the cursor back from the pointer. Done here rather than in
        // TrackCursorOwner so the change applies to the frame the press is fed, not the one after.
        if (fire.Length > 0) Widgets.PointerDrives = false;

        NavDebug.NoteKeys(fire);
    }

    /// <summary>
    /// Park a synthetic pointer at a fixed position for the whole run — <c>--pointer X,Y</c>.
    ///
    /// A development affordance, and the only way to reproduce a Deck symptom off-device. WSLg leaves
    /// the pointer outside the window unless a person is holding it there, so hover is dead in every
    /// unattended capture; gamescope always hands the app a pointer position whether or not anyone has
    /// touched the trackpad. Parking one makes the difference testable in a screenshot.
    ///
    /// It is deliberately STATIONARY: it never generates a MouseDelta, which is exactly the case that
    /// used to paint a second selector nobody could move.
    /// </summary>
    private static Vector2? _pointerPark;

    private static Vector2? ParsePointer(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var parts = raw.Split(',');
        if (parts.Length == 2 &&
            float.TryParse(parts[0], out var x) && float.TryParse(parts[1], out var y))
            return new Vector2(x, y);

        Console.Error.WriteLine($"Ignoring bad --pointer '{raw}' — expected X,Y.");
        return null;
    }

    /// <summary>
    /// Decide which input owns the cursor this frame — see <see cref="Widgets.PointerDrives"/>.
    ///
    /// Starts false: a Deck is a gamepad first, and gamescope hands the app a pointer position
    /// whether or not anyone has touched the trackpad. Treating that stationary pointer as live is
    /// what painted a highlight on whatever it happened to be resting over — a second selector that
    /// the D-pad did not move and A did not activate.
    /// </summary>
    private static void TrackCursorOwner()
    {
        var io = ImGui.GetIO();

        // The park is queued rather than assigned to io.MousePos, because NewFrame is where
        // HoveredWindow is resolved and an assignment made after it can never make a CHILD window
        // report hover — only the queue gets there in time.
        //
        // Returning early is the important half. The injection races Silk, which writes the real
        // pointer position during the same Update, so the resolved position alternates and
        // MouseDelta is non-zero on nearly every frame. A parked pointer is stationary by
        // definition, so that delta is an artifact of the harness and must never be read as the
        // user reaching for the trackpad — which is precisely the thing under test.
        if (_pointerPark is { } park)
        {
            io.AddMousePosEvent(park.X, park.Y);
            return;
        }

        // ImGui seeds MousePos at -FLT_MAX, so the first frame with a real position produces a
        // delta the size of the float range. That is the backend arriving, not the user moving.
        var d = io.MouseDelta;
        const float Absurd = 1e5f;
        bool moved = MathF.Abs(d.X) < Absurd && MathF.Abs(d.Y) < Absurd &&
                     (MathF.Abs(d.X) > 0.5f || MathF.Abs(d.Y) > 0.5f);

        if (moved || ImGui.IsMouseClicked(ImGuiMouseButton.Left) ||
            ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            Widgets.PointerDrives = true;
    }

    /// <summary>
    /// Copy to the system clipboard via SDL so the string crosses into Steam's Launch Options field
    /// (§3.3.3). SDL is already initialised by the window; SetClipboardText is process-global.
    /// </summary>
    private static unsafe bool SetClipboard(string text)
    {
        try
        {
            var sdl = SdlApi.GetApi();
            var bytes = System.Text.Encoding.UTF8.GetBytes(text + "\0");
            fixed (byte* p = bytes)
                return sdl.SetClipboardText(p) == 0;
        }
        catch { return false; }
    }

    // ImGui.NET's Text* helpers treat their argument as a printf FORMAT string, so any dynamic text
    // containing '%' (e.g. the launch command's "%command%") corrupts the output — the "?????" field.
    // TextUnformatted never formats; these route colour/wrap through it.
    private static void TextWrap(string s)
    {
        ImGui.PushTextWrapPos(0f);
        ImGui.TextUnformatted(s);
        ImGui.PopTextWrapPos();
    }

    private static void TextCol(Vector4 colour, string s)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, colour);
        ImGui.TextUnformatted(s);
        ImGui.PopStyleColor();
    }

    private static void TextDis(string s) => TextCol(Theme.TextMuted, s);

    private static string FormatAgo(TimeSpan ago)
    {
        if (ago.TotalSeconds < 60) return "just now";
        if (ago.TotalMinutes < 60) return $"{(int)ago.TotalMinutes}m ago";
        if (ago.TotalHours < 24) return $"{(int)ago.TotalHours}h ago";
        return $"{(int)ago.TotalDays}d ago";
    }
}
