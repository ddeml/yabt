using System.Text.Json.Serialization;

namespace Yabt.Core.Models;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DirectoryLinkPolicy
(
    string Behavior,
    string ExpectedTarget
)
{
    public const string FollowBehavior = "follow";
}
