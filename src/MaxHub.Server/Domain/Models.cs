using MaxHub.Core.Manifests;

namespace MaxHub.Server.Domain;

public sealed record EmployeeIdentity(string EmployeeId, string Username);

public enum QrStatus { Pending, Authorized, Consumed, Expired }

public enum ReleaseStatus { PendingReview, Published, Rejected, Withdrawn }

public sealed class ToolRelease
{
    public required string ReleaseId { get; init; }
    public required ToolManifest Manifest { get; init; }
    public required string ArtifactPath { get; init; }
    public required string Sha256 { get; init; }
    public required long SizeBytes { get; init; }
    public required string SubmittedBy { get; init; }
    public ReleaseStatus Status { get; set; } = ReleaseStatus.PendingReview;
    public string Channel { get; set; } = "internal";
    public string? ReviewedBy { get; set; }
    public DateTimeOffset SubmittedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class ConnectorRelease
{
    public required string Version { get; init; }
    public required int MinMaxYear { get; init; }
    public required int MaxMaxYear { get; init; }
    public required string ArtifactPath { get; init; }
    public required string Sha256 { get; init; }
    public required long SizeBytes { get; init; }
}

public sealed record ActivityEvent(
    string EventId,
    string EmployeeId,
    string Type,
    string Subject,
    string? ClientVersion,
    DateTimeOffset AtUtc);
