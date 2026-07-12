using System.Text.Json.Serialization;

namespace FleetWiseMobile.Models;

// Phase 8 remote camera control. Plain JSON DTOs (raw REST, not postgrest models):
// device_config = DESIRED state (driver/admin write, camera follows),
// device_status = REPORTED state (camera writes, driver reads).

public class DeviceConfigDto
{
    [JsonPropertyName("device_id")] public string DeviceId { get; set; } = "";
    [JsonPropertyName("line_ax")] public double? LineAx { get; set; }
    [JsonPropertyName("line_ay")] public double? LineAy { get; set; }
    [JsonPropertyName("line_bx")] public double? LineBx { get; set; }
    [JsonPropertyName("line_by")] public double? LineBy { get; set; }
    [JsonPropertyName("inward_sign")] public int InwardSign { get; set; } = 1;
    [JsonPropertyName("use_back_camera")] public bool UseBackCamera { get; set; }
    [JsonPropertyName("version")] public int Version { get; set; }
    [JsonPropertyName("updated_by")] public string? UpdatedBy { get; set; }
}

public class DeviceStatusDto
{
    [JsonPropertyName("device_id")] public string DeviceId { get; set; } = "";
    // Camera writes true-UTC instants; DateTimeOffset keeps the zone intact.
    [JsonPropertyName("last_seen")] public DateTimeOffset? LastSeen { get; set; }
    [JsonPropertyName("config_version_applied")] public int ConfigVersionApplied { get; set; } = -1;
    // Phase 8c/8d wake lifecycle: idle|capturing|preview|applied + when the snapshot landed.
    [JsonPropertyName("wake_state")] public string? WakeState { get; set; }
    [JsonPropertyName("snapshot_ready_at")] public DateTimeOffset? SnapshotReadyAt { get; set; }
}
