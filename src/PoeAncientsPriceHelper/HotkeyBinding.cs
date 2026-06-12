using SharpHook.Data;

namespace PoeAncientsPriceHelper;

// Pure (no-WPF, no-hook) helpers for the configurable Start/Stop hotkey, kept separate so the
// parsing/display/reserved logic is unit-testable without standing up the window or global hook.
//
// The binding is stored, captured, and matched as a single SharpHook KeyCode — the same value the
// hook reports — so there is no WPF-Key ↔ KeyCode mapping to maintain.
internal static class HotkeyBinding
{
    public const KeyCode Default = KeyCode.VcF5;

    // The three rebindable actions. Used to tell capture which binding it's replacing so it can reject
    // a key already taken by one of the *other two* (a collision check that lives in App, where the
    // current bindings are held).
    public enum Action { StartStop, Debug, Calibrate }

    public const KeyCode DefaultStartStop = KeyCode.VcF5;
    public const KeyCode DefaultDebug = KeyCode.VcF3;
    public const KeyCode DefaultCalibrate = KeyCode.VcF4;

    // Keys hard-wired to fixed gestures that mirror in-game actions (Esc closes the panel, L/R-Ctrl is
    // the buy modifier). These can never be bound to a rebindable action — a single press would fire
    // two things. Esc additionally doubles as "cancel capture". F3/F4 are NOT here anymore: they're
    // ordinary defaults now and may be rebound or reassigned between actions.
    public static readonly IReadOnlyList<KeyCode> Reserved =
    [
        KeyCode.VcEscape, KeyCode.VcLeftControl, KeyCode.VcRightControl,
    ];

    public static bool IsReserved(KeyCode key) => Reserved.Contains(key);

    // config.json round-trip: store the enum name ("VcF5") for a human-readable, int-churn-proof file.
    public static string ToStorage(KeyCode key) => key.ToString();

    public static KeyCode Parse(string? stored) =>
        Enum.TryParse<KeyCode>(stored, ignoreCase: false, out var key) && Enum.IsDefined(key)
            ? key
            : Default;

    // Friendly label for the UI: SharpHook names are "Vc"-prefixed (VcF5, VcA, Vc1) — strip it.
    public static string Display(KeyCode key)
    {
        var name = key.ToString();
        return name.StartsWith("Vc", StringComparison.Ordinal) ? name[2..] : name;
    }
}
