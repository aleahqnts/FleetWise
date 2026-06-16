using System.Text.Json;
using System.Text.Json.Serialization;
using FleetWise.Models;
using FleetWise.Models.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FleetWise.Controllers
{
    [Authorize]
    public class FleetMapController : Controller
    {
        private readonly Supabase.Client _supabase;

        public FleetMapController(Supabase.Client supabase) => _supabase = supabase;

        public async Task<IActionResult> Index()
        {
            var routesResponse = await _supabase.From<BusRoute>().Get();

            double? south = null, west = null, north = null, east = null;

            foreach (var route in routesResponse.Models)
            {
                if (string.IsNullOrWhiteSpace(route.WaypointsJson))
                    continue;

                var waypoints = JsonSerializer.Deserialize<List<WaypointDto>>(route.WaypointsJson);
                if (waypoints is null)
                    continue;

                foreach (var point in waypoints)
                {
                    south = south is null ? point.Lat : Math.Min(south.Value, point.Lat);
                    north = north is null ? point.Lat : Math.Max(north.Value, point.Lat);
                    west = west is null ? point.Lng : Math.Min(west.Value, point.Lng);
                    east = east is null ? point.Lng : Math.Max(east.Value, point.Lng);
                }
            }

            ViewBag.MapBounds = south is not null
                ? new[] { south.Value, west!.Value, north!.Value, east!.Value }
                : null;

            return View();
        }

        public async Task<IActionResult> Stops(int? routeId)
        {
            var routesResponse = await _supabase.From<BusRoute>().Get();
            var stops = new List<StopDto>();

            foreach (var route in routesResponse.Models)
            {
                if (routeId.HasValue && route.RouteId != routeId.Value)
                    continue;

                if (string.IsNullOrWhiteSpace(route.StopsJson))
                    continue;

                try
                {
                    using var doc = JsonDocument.Parse(route.StopsJson);
                    var root = doc.RootElement;

                    if (root.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var stopElement in root.EnumerateArray())
                        {
                            if (stopElement.TryGetProperty("name", out var nameElement) &&
                                stopElement.TryGetProperty("lat", out var latElement) &&
                                stopElement.TryGetProperty("lng", out var lngElement) &&
                                latElement.TryGetDouble(out var lat) &&
                                lngElement.TryGetDouble(out var lng))
                            {
                                stops.Add(new StopDto
                                {
                                    Name = nameElement.GetString() ?? "Unknown Stop",
                                    Lat = lat,
                                    Lng = lng,
                                    RouteName = route.RouteName
                                });
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error parsing stops for route {route.RouteId}: {ex.Message}");
                }
            }

            return Json(stops);
        }

        public async Task<IActionResult> Routes()
        {
            var routesResponse = await _supabase.From<BusRoute>().Get();
            var routeData = routesResponse.Models.Select(r => new
            {
                r.RouteId,
                r.RouteName,
                r.WaypointsJson
            }).ToList();

            return Json(routeData);
        }

        private class WaypointDto
        {
            [JsonPropertyName("lat")]
            public double Lat { get; set; }

            [JsonPropertyName("lng")]
            public double Lng { get; set; }
        }
    }
}
