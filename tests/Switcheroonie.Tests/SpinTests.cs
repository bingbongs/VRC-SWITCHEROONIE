using Switcheroonie.Broker;

internal static class SpinTests
{
    public static void Run(Action<bool, string> check)
    {
        void Verify(bool value, string label) => check(value, "Spin: " + label);
        var sample = new GameInputSample(Window: 73, Focused: true);
        var head = new DriverSnapshot(Alive: true, HasHead: true, HeadAge: 1, HeadX: 2, HeadY: 1.7, HeadZ: -3, HeadW: 1);
        var spin = new SpinController(); double seconds = 1;
        ulong initialGeneration = spin.Generation;
        Verify(initialGeneration > 0 && !spin.Active, "new controller has a positive inactive cancellation token");
        static double RotatedYToZ(SpinQuaternion q) => (q * new SpinQuaternion(0, 0, 1, 0) * q.Conjugate).Z;
        var forwardOnly = new SpinController();
        forwardOnly.Step(sample with { SpinToggle = true }, true, false, false, head, 1);
        forwardOnly.Step(sample with { SpinForward = true }, true, false, false, head, 1.01);
        Verify(RotatedYToZ(forwardOnly.Rotation) < 0, "Numpad8 tips the head toward OpenVR forward negative Z");
        var backwardOnly = new SpinController();
        backwardOnly.Step(sample with { SpinToggle = true }, true, false, false, head, 1);
        backwardOnly.Step(sample with { SpinBack = true }, true, false, false, head, 1.01);
        Verify(RotatedYToZ(backwardOnly.Rotation) > 0, "Numpad2 tips the head backward positive Z");
        void Step(GameInputSample s, bool eligible = true, bool desktop = false, bool typing = false)
        { seconds += .01; spin.Step(s, eligible, desktop, typing, head, seconds); }
        Step(sample with { SpinToggle = true }, false);
        Verify(!spin.Active, "disabled option rejects explicit toggle");
        Step(sample with { SpinToggle = true });
        Verify(spin.Active && spin.Generation > initialGeneration && spin.PivotX == 2 && Math.Abs(spin.PivotY - .95) < 1e-12 && spin.PivotZ == -3,
            "explicit Numpad5 captures a fixed body pivot from physical pose");
        for (int i = 0; i < 50; ++i) Step(sample with { SpinLeft = true });
        Verify(spin.RollSpeed is > .85 and < .95 && spin.PitchSpeed == 0 && spin.Rotation.Valid,
            "lateral rotation gradually accelerates from rest with a unit quaternion");
        double before = spin.RollSpeed; Step(sample);
        Verify(spin.RollSpeed > 0 && spin.RollSpeed < before, "release coasts rather than stopping instantly");
        double coastChange = before - spin.RollSpeed; before = spin.RollSpeed;
        Step(sample with { SpinRight = true });
        Verify(before - spin.RollSpeed > coastChange, "opposite direction brakes faster than coasting");
        for (int i = 0; i < 1000; ++i) Step(sample with { SpinForward = true, SpinLeft = true });
        Verify(spin.RollSpeed <= SpinController.MaximumSpeed && spin.PitchSpeed <= SpinController.MaximumSpeed && spin.Rotation.Valid,
            "combined flips and rolls remain speed bounded and normalized");
        for (int i = 0; i < 130; ++i) Step(sample);
        Verify(spin.RollSpeed == 0 && spin.PitchSpeed == 0 && spin.Active && spin.Rotation.Valid,
            "maximum speed coasts down in about a second while retaining final orientation");
        Step(sample); Verify(spin.Rotation.Valid && spin.PivotX == 2, "stationary state retains pivot and unit orientation");
        ulong stoppedGeneration = spin.Generation;
        Step(sample with { SpinToggle = true });
        Verify(!spin.Active && spin.Rotation == SpinQuaternion.Identity && spin.Generation == stoppedGeneration,
            "Numpad5 exits to natural tracking while retaining its exact cancellation token");
        Step(sample with { SpinToggle = true }, desktop: true);
        Verify(spin.Generation > stoppedGeneration, "each new activation advances the positive token");
        Step(sample with { SpinBack = true }, desktop: true);
        Verify(spin.Active && spin.PitchSpeed < 0, "desktop and VR share the same whole body controller");
        Step(sample with { Focused = false }); Verify(!spin.Active, "focus loss removes spin authority");
        Step(sample with { SpinToggle = true }); Step(sample with { Emergency = true });
        Verify(!spin.Active && spin.RollSpeed == 0, "emergency clears transform and velocity");
        Step(sample with { ChatToggle = true }); Step(sample with { SpinToggle = true });
        Verify(spin.Typing && !spin.Active, "typing cannot activate numpad motion");
        Step(sample with { ChatCancel = true }); Step(sample with { SpinToggle = true });
        Verify(spin.Active && !spin.Typing, "chat cancellation permits a new explicit toggle");
        Step(sample with { ChatToggle = true }); Step(sample with { Focused = false });
        Verify(spin.Typing && !spin.Active, "focus loss preserves chat knowledge for its original window");
        Step(sample with { Window = 74, SpinToggle = true });
        Verify(!spin.Typing && spin.Active, "a different validated game window does not inherit old chat state");
        Step(sample); Verify(!spin.Active, "window identity changes release motion");
        spin.Step(sample with { SpinToggle = true }, true, false, false, head with { HeadW = 0 }, seconds + .1);
        Verify(!spin.Active, "missing or invalid source quaternion cannot activate motion");
        var retainedEpochHead = head with { Epoch = 41 };
        var firstProcess = new SpinController();
        firstProcess.Step(sample with { SpinToggle = true }, true, false, false, retainedEpochHead, 1);
        ulong beforeRestart = firstProcess.Generation;
        firstProcess.Reset();
        var restartedProcess = new SpinController();
        ulong restartCancellation = restartedProcess.Generation;
        restartedProcess.Step(sample with { SpinToggle = true }, true, false, false, retainedEpochHead, 2);
        Verify(beforeRestart > 0 && restartCancellation > beforeRestart && restartedProcess.Generation > restartCancellation &&
            restartedProcess.Active && restartedProcess.Rotation == SpinQuaternion.Identity,
            "separate controllers on the same retained native epoch cannot reuse an attempt token");
        foreach (var pair in new[] { (0x50u, 2), (0x4Bu, 4), (0x4Cu, 5), (0x4Du, 6), (0x48u, 8) })
        {
            Verify(WindowsGameInput.NumpadKey(pair.Item1, 0) == pair.Item2, "physical numpad scans work with Num Lock on or off");
            Verify(WindowsGameInput.NumpadKey(pair.Item1, 1) == -1, "dedicated navigation keys never become numpad controls");
        }
        var presses = new KeyboardPressTracker();
        int number = WindowsGameInput.PressIdentity(0x64, 0x4B, 0);
        int navigation = WindowsGameInput.PressIdentity(0x25, 0x4B, 0);
        Verify(number == navigation, "Num Lock or Shift changes cannot change a held numpad press identity");
        presses.Observe(number, false); presses.Claim(number);
        Verify(presses.Observe(navigation, true).Swallow && !presses.IsOwned(number),
            "the same physical numpad release remains paired after virtual key interpretation changes");
        presses.Observe(0x43, false); presses.Claim(0x43);
        Verify(presses.IsOwned(0x43) && presses.Observe(0x43, true).Swallow && !presses.IsOwned(0x43),
            "previously owned posture keys can be released before applying a new mapping filter");
    }
}
