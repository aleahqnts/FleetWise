using System.Text.Json.Serialization;

namespace FleetWise.Models.ViewModels;

public class StopDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("lat")]
    public double Lat { get; set; }

    [JsonPropertyName("lng")]
    public double Lng { get; set; }

    [JsonPropertyName("routeName")]
    public string RouteName { get; set; }
}
