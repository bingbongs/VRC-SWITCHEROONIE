using Switcheroonie.Update;

namespace Switcheroonie.UI;

internal static class UpdatePresentation
{
    internal static string Text(UpdateStatus? status) => status switch
    {
        { PendingActivation: true } => "Update downloaded · applies after normal shutdown",
        { State: "Current", Message: "This portable version is current." } => "Up to date",
        { State: "Current" } => "No signed update available",
        { State: "Deferred" } => "Update check deferred",
        { State: "Unavailable" } => "Update check unavailable · try again later",
        _ => "Not checked yet"
    };
}
