using Switcheroonie.Broker;

// Exercises observable controller output only. No OS, cursor, driver, UI or game
// API is constructed; menu closure inside VRChat is intentionally unobservable.
internal static class MenuRecoveryTests
{
    internal static void Run(Action<bool, string> check)
    {
        var game = new GameInputSample(Window: 17, Focused: true);
        bool Neutral(GameInputFrame frame) => !frame.Active && !frame.Capture && !frame.Pointer && !frame.MenuEdge &&
            frame.Actions == 0 && frame.Forward == 0 && frame.Strafe == 0 && frame.Yaw == 0 && frame.Pitch == 0 &&
            frame.HandYaw == 0 && frame.HandPitch == 0;
        bool NoOutput(GameInputFrame frame) => !frame.MenuEdge && frame.Actions == 0 && frame.Forward == 0 && frame.Strafe == 0 &&
            frame.Yaw == 0 && frame.Pitch == 0 && frame.HandYaw == 0 && frame.HandPitch == 0;
        GameInputController OpenMenu(bool escape = false)
        {
            var controls = new GameInputController();
            controls.Step(game with { ActivateClick = true, LeftDown = true }, true, 1);
            controls.Step(game, true, 1);
            controls.Step(escape ? game with { EscapeMenu = true } : game with { RaiseMenu = true }, true, 1);
            controls.Step(game, true, 1);
            return controls;
        }
        foreach (bool emergency in new[] { true, false })
        foreach (bool escape in new[] { false, true })
        {
            string label = (emergency ? "Emergency" : "Full explicit release") + " after " + (escape ? "Esc" : "M");
            var controls = OpenMenu(escape);
            var before = controls.Step(game with { Forward = true, DeltaX = 40 }, true, 1);
            check(controls.MenuNavigation && before.Pointer && before.Forward == 0 && before.Yaw == 0 && before.HandYaw != 0,
                label + " starts from the reachable stale-menu output after the game closes its own menu");
            GameInputFrame release;
            if (emergency) release = controls.Step(game with { Emergency = true, Forward = true, DeltaX = 40, RaiseMenu = true }, true, 1);
            else { controls.Release(resetMenuNavigation: true); release = controls.Step(game with { Forward = true, DeltaX = 40 }, true, 1); }
            check(Neutral(release) && !controls.MenuNavigation && !controls.Active,
                label + " clears local menu/ownership without a menu edge, action or pointer motion");
            check(Neutral(controls.Step(game with { Forward = true, DeltaX = 40 }, true, 1)),
                label + " cannot reacquire through continuing movement or mouse look");
            var acquire = controls.Step(game with { ActivateClick = true, LeftDown = true, Forward = true }, true, 1);
            check(acquire.Active && acquire.Capture && !acquire.Pointer && NoOutput(acquire),
                label + " fresh click selects ordinary look and consumes held movement");
            controls.Step(game, true, 1);
            var resumed = controls.Step(game with { Forward = true, Right = true, DeltaX = 40, DeltaY = -20 }, true, 1);
            check(resumed.Active && resumed.Capture && !resumed.Pointer && !resumed.MenuEdge && resumed.Actions == 0 &&
                resumed.Forward == 1 && resumed.Strafe == 1 && Math.Abs(resumed.Yaw - .1) < 1e-12 &&
                Math.Abs(resumed.Pitch - .05) < 1e-12 && resumed.HandYaw == 0 && resumed.HandPitch == 0,
                label + " restores fresh WASD/head look instead of silently returning to menu navigation");
        }
        var held = OpenMenu();
        var allHeld = game with { LeftDown = true, RightDown = true, MiddleDown = true, Forward = true, Right = true,
            Jump = true, Run = true, Crouch = true, Prone = true };
        held.Step(allHeld, true, 1);
        held.Release(resetMenuNavigation: true);
        check(Neutral(held.Step(allHeld with { ActivateClick = true, EscapeMenu = true, DeltaX = 100 }, true, 1)) && !held.MenuNavigation,
            "Full recovery refuses a premature raw activation/menu edge while the old mouse button is still held");
        check(Neutral(held.Step(allHeld with { LeftDown = false }, true, 1)),
            "Releasing the old activation button clears its wait gate without acquiring or pulsing the menu");
        check(Neutral(held.Step(allHeld with { LeftDown = false }, true, 1)),
            "Held keyboard/right/middle buttons alone cannot reacquire after recovery");
        var fresh = held.Step(allHeld with { ActivateClick = true }, true, 1);
        check(fresh.Active && fresh.Capture && !fresh.Pointer && NoOutput(fresh),
            "Recovery acquisition gates every already-held interaction, posture and movement key");
        var gated = held.Step(allHeld, true, 1);
        check(gated.Active && !gated.Pointer && NoOutput(gated),
            "Held right/middle/left buttons, WASD, jump, run and posture remain neutral after recovery acquisition");
        held.Step(game, true, 1);
        var normal = held.Step(game with { LeftDown = true, MiddleDown = true, Forward = true, Right = true, Jump = true, Run = true,
            Crouch = true, DeltaX = 40 }, true, 1);
        check(normal.Active && !normal.Pointer && !normal.MenuEdge && normal.Forward == 1 && normal.Strafe == 1 && normal.Actions == 59 && normal.Yaw != 0,
            "Fresh controls after physical release work normally, including a new posture toggle");

        foreach (bool focusLoss in new[] { true, false })
        {
            var retained = OpenMenu();
            if (focusLoss) retained.Step(game with { Focused = false }, true, 1); else retained.Release();
            check(retained.MenuNavigation && !retained.Active,
                (focusLoss ? "Ordinary focus loss" : "Ordinary ownership release") + " retains the intentional M/Esc menu state");
            var returned = retained.Step(game with { ActivateClick = true, LeftDown = true }, true, 1);
            check(returned.Active && returned.Pointer && NoOutput(returned),
                "Ordinary release returns to retained menu navigation only through a fresh click");
        }
        var sharedMenu = OpenMenu();
        var closed = sharedMenu.Step(game with { EscapeMenu = true }, true, 1);
        check(!sharedMenu.MenuNavigation && closed.MenuEdge, "M opening and Esc closing still share one menu state");
        sharedMenu.Step(game, true, 1);
        var opened = sharedMenu.Step(game with { RaiseMenu = true }, true, 1);
        check(sharedMenu.MenuNavigation && opened.MenuEdge, "M still opens the same state after normal Esc closure");

        var chat = OpenMenu();
        chat.Step(game with { ChatToggle = true }, true, 1);
        chat.Release(resetMenuNavigation: true);
        var typing = chat.Step(game with { ActivateClick = true, LeftDown = true, Forward = true, DeltaX = 40 }, true, 1);
        check(chat.Typing && !chat.MenuNavigation && !chat.Active && Neutral(typing),
            "Local menu recovery preserves known open chat and cannot capture through its click");
        var cancelled = chat.Step(game with { ChatCancel = true }, true, 1);
        check(!chat.Typing && !chat.MenuNavigation && Neutral(cancelled),
            "Observed chat cancellation after recovery clears typing without creating a native menu edge");
    }
}
