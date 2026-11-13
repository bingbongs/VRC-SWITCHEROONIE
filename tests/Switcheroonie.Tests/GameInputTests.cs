using Switcheroonie.Broker;

// These tests operate only on the pure controller. They do not read keyboard or
// mouse state, install hooks, change cursor capture, or find/launch a game.
public static class GameInputTests
{
    public static int Run(Action<bool, string> check)
    {
        int checks = 0;
        void Verify(bool condition, string label)
        {
            ++checks;
            check(condition, "Game input: " + label);
        }
        static bool Near(double actual, double expected) => Math.Abs(actual - expected) < 1e-12;
        static bool NoOutput(GameInputFrame f) => f.Yaw == 0 && f.Pitch == 0 &&
            f.HandYaw == 0 && f.HandPitch == 0 && f.Forward == 0 && f.Strafe == 0 && f.Actions == 0;
        static bool Neutral(GameInputFrame f) => f == default;
        var game = new GameInputSample(Window: 41, Focused: true);
        GameInputController Ready()
        {
            var controller = new GameInputController();
            controller.Step(game with { LeftDown = true, ActivateClick = true }, true, 1);
            controller.Step(game, true, 1);
            return controller;
        }

        var first = new GameInputController();
        Verify(Neutral(first.Step(game with { Forward = true, LeftDown = true, DeltaX = 80 }, true, 1)),
            "polling a held left button cannot acquire control");
        var heldAtClick = game with
        {
            ActivateClick = true, LeftDown = true, RightDown = true, MiddleDown = true,
            Forward = true, Right = true, Jump = true, Run = true, DeltaX = 40, DeltaY = 30
        };
        var frame = first.Step(heldAtClick, true, 1);
        Verify(frame.Active && frame.Capture && frame.Window == game.Window && NoOutput(frame),
            "authenticated activation consumes click, motion, and already held keys");
        frame = first.Step(heldAtClick with { ActivateClick = false }, true, 1);
        Verify(frame.Active && frame.Forward == 0 && frame.Strafe == 0 && frame.Actions == 0,
            "movement and every held button remain gated after activation");
        first.Step(game, true, 1);
        frame = first.Step(game with { Forward = true, Right = true, Jump = true, Run = true, LeftDown = true, MiddleDown = true }, true, 1);
        Verify(frame.Forward == 1 && frame.Strafe == 1 && frame.Actions == (1u | 2u | 8u | 16u),
            "release then repress restores movement, use, grab, jump, and run");
        Verify(NoOutput(first.Step(game, true, 1)), "button and movement releases publish neutral controls");

        var quickClick = new GameInputController();
        frame = quickClick.Step(game with { ActivateClick = true, LeftDown = false, DeltaX = 50 }, true, 1);
        Verify(frame.Active && frame.Capture && NoOutput(frame),
            "a complete short raw click activates even when polling sees its button already up");
        frame = quickClick.Step(game with { LeftDown = true }, true, 1);
        Verify(frame.Actions == 1, "the next click after a short activation click performs use");

        var look = Ready();
        frame = look.Step(game with { DeltaX = 50, DeltaY = 20 }, true, 1);
        Verify(frame.Active && frame.Capture && Near(frame.Yaw, 0.125) && Near(frame.Pitch, -0.05),
            "normal mouse movement looks without requiring right mouse");
        Verify(frame.HandYaw == 0 && frame.HandPitch == 0 && frame.Actions == 0,
            "head look does not move the hand or press a controller button");
        frame = look.Step(game with { DeltaX = -50, DeltaY = -20 }, true, 2);
        Verify(Near(frame.Yaw, -0.25) && Near(frame.Pitch, 0.1), "sensitivity scales signed yaw and pitch deltas once");
        Verify(NoOutput(look.Step(game, true, 1)), "a frame with no mouse motion never repeats prior deltas");
        frame = look.Step(game with { Forward = true, Back = true, Left = true, Right = true }, true, 1);
        Verify(frame.Forward == 0 && frame.Strafe == 0, "opposite movement keys cancel");
        frame = look.Step(game with { Back = true, Left = true }, true, 1);
        Verify(frame.Forward == -1 && frame.Strafe == -1, "back and left retain the expected axis signs");
        frame = look.Step(game with { MiddleDown = true }, true, 1);
        Verify(frame.Actions == 2, "middle mouse generates grab independently of head look");

        var posture = Ready();
        frame = posture.Step(game with { Crouch = true, Forward = true, Run = true }, true, 1);
        Verify(frame.Actions == (32u | 16u) && frame.Forward == 1,
            "first crouch press toggles posture alongside movement");
        Verify(posture.Step(game with { Crouch = true }, true, 1).Actions == 32,
            "a held crouch key cannot repeat the toggle");
        Verify(posture.Step(game, true, 1).Actions == 32, "crouch remains after key release");
        Verify(posture.Step(game with { Crouch = true }, true, 1).Actions == 0, "second crouch press stands");
        posture.Step(game, true, 1);
        Verify(posture.Step(game with { CrouchPress = true }, true, 1).Actions == 32, "a complete short press between polls toggles crouch");
        frame = posture.Step(game with { Crouch = true, Prone = true }, true, 1);
        Verify(frame.Actions == 64u, "prone toggles out of crouch and wins simultaneous edges");
        Verify(posture.Step(game, true, 1).Actions == 64, "prone remains after key release");
        Verify(posture.Step(game with { Prone = true }, true, 1).Actions == 0, "second prone press stands");
        posture.Step(game, true, 1);
        frame = posture.Step(game with { RightDown = true, DeltaX = 40, DeltaY = 20 }, true, 1);
        Verify(frame.Capture && frame.Pointer && frame.Actions == 0 && frame.Yaw == 0 && frame.Pitch == 0 &&
            Near(frame.HandYaw, .1) && Near(frame.HandPitch, -.05),
            "held right mouse uses a bounded interaction pointer without pressing grab or moving the head");
        frame = posture.Step(game with { DeltaX = 40 }, true, 1);
        Verify(frame.Capture && !frame.Pointer && Near(frame.Yaw, .1) && frame.HandYaw == 0,
            "releasing right mouse restores centered head look");
        frame = posture.Step(game with { EscapeMenu = true, Crouch = true, Prone = true, RightDown = true, DeltaX = 40 }, true, 1);
        Verify(frame.MenuEdge && frame.Pointer && NoOutput(frame), "Esc menu transition consumes held posture and pointer motion");
        frame = posture.Step(game with { Crouch = true, Prone = true }, true, 1);
        Verify(frame.Actions == 0, "menu navigation cannot retain a crouched or prone pose modifier");
        frame = posture.Step(game with { EscapeMenu = true, Crouch = true, Prone = true }, true, 1);
        Verify(frame.MenuEdge && !frame.Pointer && NoOutput(frame), "second Esc returns to look with posture still neutral");
        Verify(posture.Step(game with { Crouch = true, Prone = true }, true, 1).Actions == 0,
            "posture held across menu close requires release before resuming");
        posture.Step(game, true, 1);
        Verify(posture.Step(game with { Prone = true }, true, 1).Actions == 64u, "fresh prone press works after menu release");
        Verify(Neutral(posture.Step(game with { Focused = false, Prone = true }, true, 1)),
            "focus loss clears posture and pointer ownership");
        frame = posture.Step(game with { ActivateClick = true, LeftDown = true, Crouch = true, Prone = true }, true, 1);
        Verify(frame.Active && NoOutput(frame), "reacquisition consumes posture already held during the fresh click");
        Verify(posture.Step(game with { Crouch = true, Prone = true }, true, 1).Actions == 0,
            "a posture held through reacquisition stays neutral until released");

        var menu = Ready();
        frame = menu.Step(game with { RaiseMenu = true }, true, 1);
        Verify(menu.MenuNavigation && frame.MenuEdge && frame.Pointer, "M raises the persistent left menu hand");
        menu.Step(game, true, 1);
        frame = menu.Step(game with { EscapeMenu = true }, true, 1);
        Verify(!menu.MenuNavigation && frame.MenuEdge, "Esc closes the same menu state and lowers the left hand");
        foreach (var stance in new[] { (Crouch: true, Prone: false, Actions: 32u), (Crouch: false, Prone: true, Actions: 64u) })
        {
            var heldPosture = Ready();
            heldPosture.Step(game with { Crouch = stance.Crouch, Prone = stance.Prone }, true, 1);
            heldPosture.Step(game, true, 1);
            frame = heldPosture.Step(game with { RaiseMenu = true, Forward = true, Jump = true, Run = true, LeftDown = true, DeltaX = 30 }, true, 1);
            Verify(frame.MenuEdge && frame.Actions == stance.Actions && frame.Forward == 0 && frame.Yaw == 0,
                "opening M menu retains the toggled posture without movement or interaction");
            heldPosture.Step(game, true, 1);
            frame = heldPosture.Step(game with { EscapeMenu = true, Forward = true, RightDown = true }, true, 1);
            Verify(frame.MenuEdge && frame.Actions == stance.Actions && frame.Forward == 0,
                "closing Esc menu retains posture without a standing-height jump");
        }
        menu.Step(game, true, 1);
        frame = menu.Step(game with { ToggleMenu = true, Forward = true, LeftDown = true, DeltaX = 50, DeltaY = 20 }, true, 1);
        Verify(menu.MenuNavigation && frame.Active && frame.Capture && frame.Pointer && frame.MenuEdge,
            "menu toggle selects client-bounded pointer capture and reports one menu edge");
        Verify(NoOutput(frame), "menu transition consumes motion and all actions on its edge");
        frame = menu.Step(game with { Forward = true, LeftDown = true, DeltaX = 50, DeltaY = 20 }, true, 1);
        Verify(frame.Actions == 0 && !frame.MenuEdge, "a held click cannot select immediately after opening a menu");
        menu.Step(game, true, 1);
        frame = menu.Step(game with { Forward = true, Right = true, Jump = true, Run = true, LeftDown = true, DeltaX = 50, DeltaY = 20 }, true, 1);
        Verify(frame.Forward == 0 && frame.Strafe == 0 && frame.Yaw == 0 && frame.Pitch == 0,
            "menu navigation suppresses locomotion and head look");
        Verify(Near(frame.HandYaw, 0.125) && Near(frame.HandPitch, -0.05) && frame.Actions == 1,
            "menu mouse movement aims the hand and a fresh left press selects or drags");
        Verify(frame.Capture && frame.Pointer && !frame.MenuEdge && frame.Active, "menu selection keeps the pointer in the client and does not repeat the toggle");
        frame = menu.Step(game with { ToggleMenu = true, Forward = true, LeftDown = true, DeltaX = 70 }, true, 1);
        Verify(!menu.MenuNavigation && frame.MenuEdge && frame.Capture && NoOutput(frame),
            "closing a menu returns to capture while consuming the transition inputs");
        frame = menu.Step(game with { Forward = true, LeftDown = true }, true, 1);
        Verify(frame.Forward == 0 && frame.Actions == 0, "keys and click held across menu close remain gated");
        menu.Step(game, true, 1);
        frame = menu.Step(game with { Forward = true, DeltaX = 20 }, true, 1);
        Verify(frame.Forward == 1 && Near(frame.Yaw, 0.05) && frame.HandYaw == 0,
            "fresh movement and head look resume after the menu-close release");
        menu.ToggleMenu();
        frame = menu.Step(game with { Forward = true, LeftDown = true }, true, 1);
        Verify(menu.MenuNavigation && frame.Forward == 0 && frame.Actions == 0,
            "a broker-requested menu toggle also gates held controls");

        var chat = Ready();
        frame = chat.Step(game with { ChatToggle = true, Forward = true, Jump = true, LeftDown = true, DeltaX = 80 }, true, 1);
        Verify(chat.Typing && !chat.Active && Neutral(frame), "opening chat releases capture and publishes no gameplay input");
        frame = chat.Step(game with { Forward = true, Jump = true, LeftDown = true, DeltaX = 80, ToggleMenu = true }, true, 1);
        Verify(Neutral(frame) && chat.Typing && !chat.MenuNavigation, "typing blocks keys, pointer motion, and menu toggles");
        frame = chat.Step(game with { ChatCancel = true, Forward = true, Jump = true, LeftDown = true, DeltaY = 80 }, true, 1);
        Verify(!chat.Typing && chat.Active && Neutral(frame), "Esc chat cancellation consumes its frame and clears typing");
        frame = chat.Step(game with { Forward = true, Jump = true, LeftDown = true }, true, 1);
        Verify(frame.Active && frame.Capture && frame.Forward == 0 && frame.Actions == 0,
            "Esc chat cancellation leaves held movement and buttons neutral until release");
        chat.Step(game, true, 1);
        frame = chat.Step(game with { Forward = true, Jump = true, LeftDown = true }, true, 1);
        Verify(frame.Forward == 1 && frame.Actions == (1u | 8u), "fresh controls work after cancelling chat and releasing keys");
        chat.Step(game with { ChatToggle = true }, true, 1);
        frame = chat.Step(game with { ChatToggle = true, Run = true }, true, 1);
        Verify(!chat.Typing && Neutral(frame), "Enter also closes typing without producing a gameplay action");
        Verify(chat.Step(game with { Run = true }, true, 1).Actions == 0, "run held while submitting chat remains gated");

        var uncapturedChat = new GameInputController();
        frame = uncapturedChat.Step(game with { ChatToggle = true, Forward = true }, true, 1);
        Verify(Neutral(frame) && uncapturedChat.Typing && !uncapturedChat.Active,
            "chat opened before capture is tracked without acquiring gameplay input");
        frame = uncapturedChat.Step(game with { ActivateClick = true, LeftDown = true, Forward = true, DeltaX = 100 }, true, 1);
        Verify(Neutral(frame) && uncapturedChat.Typing && !uncapturedChat.Active,
            "a click inside pre-capture chat cannot capture look or movement");
        frame = uncapturedChat.Step(game with { ChatCancel = true, LeftDown = true, Forward = true }, true, 1);
        Verify(Neutral(frame) && !uncapturedChat.Typing && !uncapturedChat.Active,
            "cancelling pre-capture chat leaves gameplay released");
        Verify(Neutral(uncapturedChat.Step(game with { Forward = true }, true, 1)),
            "chat cancellation does not automatically capture the game");
        frame = uncapturedChat.Step(game with { ActivateClick = true, LeftDown = true, Forward = true }, true, 1);
        Verify(frame.Active && NoOutput(frame), "a fresh click after pre-capture chat cancellation consumes held controls");

        foreach (string boundary in new[] { "focus loss", "emergency release", "explicit release" })
        {
            var retainedChat = Ready();
            retainedChat.Step(game with { ChatToggle = true }, true, 1);
            if (boundary == "focus loss") frame = retainedChat.Step(game with { Focused = false, Forward = true }, true, 1);
            else if (boundary == "emergency release") frame = retainedChat.Step(game with { Emergency = true, Forward = true }, true, 1);
            else { retainedChat.Release(); frame = retainedChat.Step(game with { Forward = true }, true, 1); }
            Verify(Neutral(frame) && retainedChat.Typing && !retainedChat.Active,
                boundary + " preserves known open chat while releasing gameplay ownership");
            frame = retainedChat.Step(game with { ActivateClick = true, LeftDown = true, Forward = true, DeltaX = 90 }, true, 1);
            Verify(Neutral(frame) && retainedChat.Typing && !retainedChat.Active,
                boundary + " cannot turn a click in the still-open chat into gameplay capture");
            frame = retainedChat.Step(game with { ChatCancel = true, LeftDown = true, Forward = true }, true, 1);
            Verify(Neutral(frame) && !retainedChat.Typing && !retainedChat.Active,
                boundary + " requires observed chat cancellation before capture is available again");
            retainedChat.Step(game, true, 1);
            frame = retainedChat.Step(game with { ActivateClick = true, LeftDown = true, Forward = true }, true, 1);
            Verify(frame.Active && NoOutput(frame), boundary + " resumes only from a new safe click after chat cancellation");
        }
        var differentChatWindow = Ready();
        differentChatWindow.Step(game with { ChatToggle = true }, true, 1);
        frame = differentChatWindow.Step(game with { Window = 42 }, true, 1);
        Verify(Neutral(frame) && !differentChatWindow.Typing && !differentChatWindow.Active,
            "known chat state does not transfer to a different eligible game window");
        frame = differentChatWindow.Step(game with { Window = 42, ActivateClick = true, LeftDown = true }, true, 1);
        Verify(frame.Active && NoOutput(frame), "the different game window acquires from its own new click");
        differentChatWindow.Step(game with { Window = 42, ChatToggle = true }, true, 1);
        differentChatWindow.Reset();
        Verify(!differentChatWindow.Typing && !differentChatWindow.Active,
            "a complete reset clears known typing and ownership together");

        var chatOnly = new GameInputController();
        frame = chatOnly.Step(game with { ChatToggle = true, ActivateClick = true, LeftDown = true, Forward = true, DeltaX = 90 }, false, 1, observeChat: true);
        Verify(chatOnly.Typing && !chatOnly.Active && Neutral(frame),
            "spin-only chat observation cannot acquire held gameplay or mouse movement");
        frame = chatOnly.Step(game with { ChatCancel = true, LeftDown = true, Forward = true, ToggleMenu = true }, false, 1, observeChat: true);
        Verify(!chatOnly.Typing && !chatOnly.Active && !chatOnly.MenuNavigation && Neutral(frame),
            "spin-only cancellation clears chat without opening menus or asserting held inputs");
        frame = chatOnly.Step(game with { ActivateClick = true, LeftDown = true, Forward = true, DeltaY = 90 }, false, 1, observeChat: true);
        Verify(!chatOnly.Active && Neutral(frame), "chat observation alone never grants movement or cursor authority");
        frame = chatOnly.Step(game with { LeftDown = true, Forward = true }, true, 1);
        Verify(!chatOnly.Active && Neutral(frame), "returning to Desktop still needs a fresh authenticated click");
        frame = chatOnly.Step(game with { Focused = false, ChatToggle = true }, false, 1, observeChat: true);
        Verify(!chatOnly.Typing && Neutral(frame), "unfocused samples cannot change even the spin-only chat state");

        var focus = Ready();
        frame = focus.Step(game with { Focused = false, LeftDown = true, Forward = true, DeltaX = 40 }, true, 1);
        Verify(Neutral(frame) && !focus.Active, "focus loss immediately clears ownership and output");
        frame = focus.Step(game with { LeftDown = true, Forward = true, DeltaX = 40 }, true, 1);
        Verify(Neutral(frame), "a left button held across focus loss cannot recapture the game");
        focus.Step(game, true, 1);
        frame = focus.Step(game with { ActivateClick = true, LeftDown = true }, true, 1);
        Verify(frame.Active && NoOutput(frame), "a new raw click after focus return reacquires safely");
        frame = focus.Step(game with { Window = 42, LeftDown = true, Forward = true }, true, 1);
        Verify(Neutral(frame) && !focus.Active, "switching window identity cannot transfer ownership through a held click");
        frame = focus.Step(game with { Window = 42, ActivateClick = true, LeftDown = false }, true, 1);
        Verify(frame.Active && frame.Window == 42 && NoOutput(frame), "a new eligible window needs its own authenticated activation click");

        var emergency = Ready();
        emergency.Step(game with { Forward = true, LeftDown = true }, true, 1);
        frame = emergency.Step(game with { Emergency = true, Forward = true, LeftDown = true, DeltaX = 40 }, true, 1);
        Verify(Neutral(frame) && !emergency.Active, "emergency release overrides held controls and motion");
        Verify(Neutral(emergency.Step(game with { LeftDown = true, Forward = true }, true, 1)),
            "emergency release cannot reacquire from a held activation button");
        Verify(Neutral(emergency.Step(game with { ActivateClick = true, LeftDown = true }, true, 1)),
            "emergency hold gate rejects a premature activation edge before button release");
        emergency.Step(game, true, 1);
        Verify(Neutral(emergency.Step(game with { Forward = true }, true, 1)), "releasing the emergency button alone does not reacquire");
        frame = emergency.Step(game with { ActivateClick = true, LeftDown = true, Forward = true }, true, 1);
        Verify(frame.Active && NoOutput(frame), "a fresh click after emergency release reacquires and consumes held keys");
        emergency.Reset();
        Verify(!emergency.Active && !emergency.Typing && !emergency.MenuNavigation,
            "reset clears ownership, typing, and menu navigation");

        foreach (string context in new[] { "Physical source", "unrelated foreground process" })
        {
            var ignored = Ready();
            frame = ignored.Step(game with { ActivateClick = true, LeftDown = true, Forward = true, ToggleMenu = true, ChatToggle = true, DeltaX = 100 }, false, 1);
            Verify(Neutral(frame) && !ignored.Active && !ignored.Typing && !ignored.MenuNavigation,
                context + " ignores click, key, chat, menu, and delta events");
            Verify(Neutral(ignored.Step(game with { LeftDown = true }, true, 1)),
                context + " cannot leave a polled button armed when eligibility returns");
        }
        var invalid = Ready();
        Verify(Neutral(invalid.Step(game with { Window = 0, ActivateClick = true, LeftDown = true, ToggleMenu = true }, true, 1)) && !invalid.Active,
            "a zero window never owns input even with an activation event");

        var bounded = Ready();
        frame = bounded.Step(game with { DeltaX = int.MaxValue, DeltaY = int.MinValue }, true, 5);
        Verify(Near(frame.Yaw, 25) && Near(frame.Pitch, 25) && double.IsFinite(frame.Yaw) && double.IsFinite(frame.Pitch),
            "pathological positive and negative pixel deltas clamp before maximum sensitivity");
        frame = bounded.Step(game with { DeltaX = int.MinValue, DeltaY = int.MaxValue }, true, 0.1);
        Verify(Near(frame.Yaw, -0.5) && Near(frame.Pitch, -0.5), "delta bounds also preserve signs at minimum sensitivity");
        bounded.ToggleMenu();
        frame = bounded.Step(game with { DeltaX = int.MaxValue, DeltaY = int.MinValue }, true, 5);
        Verify(frame.Yaw == 0 && frame.Pitch == 0 && Near(frame.HandYaw, 25) && Near(frame.HandPitch, 25),
            "menu hand deltas use the same finite pixel bounds without head movement");
        Verify(NoOutput(bounded.Step(game, true, 1)), "clamped motion also never leaks into later frames");

        return checks;
    }
}
