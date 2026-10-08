namespace Switcheroonie.Broker;

public readonly record struct GameInputSample(long Window = 0, bool Focused = false,
    bool LeftDown = false, bool RightDown = false, bool MiddleDown = false,
    bool Forward = false, bool Back = false, bool Left = false, bool Right = false,
    bool Jump = false, bool Run = false, int DeltaX = 0, int DeltaY = 0,
    bool ToggleMenu = false, bool ChatToggle = false, bool ChatCancel = false, bool Emergency = false,
    bool ActivateClick = false, bool Crouch = false, bool Prone = false, bool EscapeMenu = false,
    bool RaiseMenu = false, bool SpinToggle = false, bool SpinLeft = false, bool SpinRight = false,
    bool SpinForward = false, bool SpinBack = false, bool CrouchPress = false, bool PronePress = false);

public interface IGameInputPlatform : IDisposable
{
    GameInputSample Read(bool eligible);
    void SetCapture(bool capture);
    void SetCapture(bool capture, bool pointer) => SetCapture(capture);
    void SetTyping(bool typing) { }
    void SetActive(bool active) { }
    void SetSpinEnabled(bool enabled) { }
    void ConfigureKeys(IReadOnlyDictionary<string, int> keys) { }
}

public readonly record struct GameInputFrame(bool Active = false, bool Capture = false,
    long Window = 0, double Yaw = 0, double Pitch = 0, double HandYaw = 0, double HandPitch = 0,
    double Forward = 0, double Strafe = 0, uint Actions = 0, bool MenuEdge = false, bool Pointer = false);

// Pure ownership state machine: no OS input, cursor operations or game processes.
// Click acquisition consumes the first click and gates every held key until release.
public sealed class GameInputController
{
    long window;
    bool active, lastLeft, activationClick, waitForRelease, typing;
    uint heldGate;
    uint posture;
    bool lastCrouch, lastProne;
    public bool MenuNavigation { get; private set; }
    public bool Typing => typing;
    public bool Active => active && !typing;
    public void Release(bool requireFreshClick = true)
    {
        active = false; activationClick = false; heldGate = 0;
        posture = 0; lastCrouch = lastProne = false;
        // An emergency while clicking cannot immediately acquire again.
        waitForRelease = requireFreshClick && lastLeft;
    }
    public void Reset() { Release(); typing = false; MenuNavigation = false; window = 0; }
    public void ToggleMenu() { MenuNavigation = !MenuNavigation; typing = false; heldGate = uint.MaxValue; }
    static uint Held(GameInputSample s) => (s.Forward ? 1u : 0) | (s.Back ? 2u : 0) | (s.Left ? 4u : 0) |
        (s.Right ? 8u : 0) | (s.Jump ? 16u : 0) | (s.Run ? 32u : 0) |
        (s.LeftDown ? 64u : 0) | (s.RightDown ? 128u : 0) | (s.MiddleDown ? 256u : 0) |
        (s.Crouch ? 512u : 0) | (s.Prone ? 1024u : 0);

    public GameInputFrame Step(GameInputSample s, bool eligible, double sensitivity, bool observeChat = false)
    {
        lastLeft = s.LeftDown;
        if (s.Emergency) { Release(); return new(); }
        if ((!eligible && !observeChat) || !s.Focused || s.Window == 0)
        {
            Release();
            // No stale event/delta or button survives a focus or source boundary.
            lastLeft = false; waitForRelease = false; return new();
        }
        if (window != s.Window)
        {
            if (window != 0) Reset(); else Release(false);
            waitForRelease = false; window = s.Window;
        }
        // Independent VR spin still needs authenticated chat edges. Observing
        // those edges never acquires Desktop movement, buttons or cursor input.
        if (!eligible)
        {
            Release(); lastLeft = false; waitForRelease = false;
        }
        if (waitForRelease)
        {
            if (!s.LeftDown) waitForRelease = false;
            return new();
        }
        uint held = Held(s);
        if (s.ChatCancel && typing) { typing = false; heldGate = held; return new(); }
        if (s.ChatToggle) { typing = !typing; heldGate = held; return new(); }
        if (typing) return new();
        if (!eligible) return new();
        if (!active)
        {
            if (!s.ActivateClick) return new();
            active = true; activationClick = s.LeftDown; heldGate = held;
            return new(Active: true, Capture: true, Window: window, Pointer: MenuNavigation);
        }
        heldGate &= held;
        if (!s.LeftDown) activationClick = false;
        bool menuEdge = s.ToggleMenu || s.EscapeMenu || s.RaiseMenu;
        if (menuEdge) { ToggleMenu(); heldGate = held; }
        uint allowed = held & ~heldGate;
        bool crouchEdge = s.CrouchPress || s.Crouch && !lastCrouch && (allowed & 512) != 0;
        bool proneEdge = s.PronePress || s.Prone && !lastProne && (allowed & 1024) != 0;
        lastCrouch = s.Crouch; lastProne = s.Prone;
        if (!MenuNavigation && !menuEdge)
        {
            if (proneEdge) posture = posture == 64 ? 0u : 64u;
            else if (crouchEdge) posture = posture == 32 ? 0u : 32u;
        }
        uint actions = (allowed & 64) != 0 && !activationClick ? 1u : 0;
        if ((allowed & 256u) != 0) actions |= 2;
        bool pointer = MenuNavigation || (allowed & 128u) != 0;
        double forward = 0, strafe = 0;
        if (!MenuNavigation)
        {
            forward = ((allowed & 1) != 0 ? 1 : 0) - ((allowed & 2) != 0 ? 1 : 0);
            strafe = ((allowed & 8) != 0 ? 1 : 0) - ((allowed & 4) != 0 ? 1 : 0);
            if ((allowed & 16) != 0) actions |= 8;
            if ((allowed & 32) != 0) actions |= 16;
        }
        actions |= posture;
        // Bound pathological device deltas before converting pixels to radians.
        double dx = Math.Clamp(s.DeltaX, -2000, 2000) * 0.0025 * sensitivity;
        double dy = -Math.Clamp(s.DeltaY, -2000, 2000) * 0.0025 * sensitivity;
        return new(true, true, window,
            pointer || menuEdge ? 0 : dx, pointer || menuEdge ? 0 : dy,
            pointer && !menuEdge ? dx : 0, pointer && !menuEdge ? dy : 0,
            forward, strafe, menuEdge ? posture : actions, menuEdge, pointer);
    }
}

public sealed class NullGameInputPlatform : IGameInputPlatform
{
    public GameInputSample Read(bool eligible) => new();
    public void SetCapture(bool capture) { }
    public void Dispose() { }
}
