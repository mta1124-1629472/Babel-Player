using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Babel.Player.Models;

public sealed class ArtifactIntegrityManifest
{
    [JsonPropertyName("manifest_version")]
    public string? ManifestVersion { get; set; }

    [JsonPropertyName("artifact_kind")]
    public string? ArtifactKind { get; set; }

    [JsonPropertyName("artifact_schema_version")]
    public string? ArtifactSchemaVersion { get; set; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }

    [JsonPropertyName("file_size_bytes")]
    public long FileSizeBytes { get; set; }

    [JsonPropertyName("probed_duration_seconds")]
    public double? ProbedDurationSeconds { get; set; }

    [JsonPropertyName("segment_count")]
    public int? SegmentCount { get; set; }

    [JsonPropertyName("segment_ids")]
    public List<string>? SegmentIds { get; set; }

    [JsonPropertyName("segment_timing")]
    public ArtifactSegmentTimingSummary? SegmentTiming { get; set; }

    [JsonPropertyName("upstream_artifact_hashes")]
    public Dictionary<string, string>? UpstreamArtifactHashes { get; set; }

    [JsonPropertyName("provenance_digest")]
    public string? ProvenanceDigest { get; set; }
}

public sealed class ArtifactSegmentTimingSummary
{
    [JsonPropertyName("start_seconds")]
    public double StartSeconds { get; set; }

    [JsonPropertyName("end_seconds")]
    public double EndSeconds { get; set; }

    [JsonPropertyName("duration_seconds")]
    public double DurationSeconds { get; set; }
}
