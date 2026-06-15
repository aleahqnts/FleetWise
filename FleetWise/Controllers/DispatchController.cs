using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using FleetWise.Models;
using FleetWise.ViewModels;
using static Postgrest.Constants;


namespace FleetWise.Controllers
{
    [Authorize]
    public class DispatchController : Controller
    {
        private readonly Supabase.Client _supabase;

        public DispatchController(Supabase.Client supabase)
        {
            _supabase = supabase;
        }

        public async Task<IActionResult> Index()
        {
            // --- Always scope dispatch to today's date ---
            var today = DateTime.Today.ToString("yyyy-MM-dd");

            // --- Fetch all data in parallel ---
            var tripsTask = _supabase.From<Trip>()
                                       .Filter("date", Operator.Equals, today)
                                       .Get();
            var vehiclesTask = _supabase.From<Vehicle>().Get();
            var routesTask = _supabase.From<BusRoute>().Get();
            var driversTask = _supabase.From<UserModel>()
                                       .Filter("role_id", Operator.Equals, "2")
                                       .Filter("account_status", Operator.Equals, "Activated")
                                       .Get();
            var availabilityTask = _supabase.From<DriverAvailability>().Get();

            await Task.WhenAll(tripsTask, vehiclesTask, routesTask, driversTask, availabilityTask);

            var trips = tripsTask.Result.Models;
            var vehicles = vehiclesTask.Result.Models;
            var routes = routesTask.Result.Models;
            var drivers = driversTask.Result.Models;
            var availability = availabilityTask.Result.Models;

            // --- Build lookup dictionaries ---
            var vehicleDict = vehicles.ToDictionary(v => v.VehicleId);
            var driverDict = drivers.ToDictionary(d => d.UserId);
            var availabilityDict = availability.ToDictionary(a => a.UserId, a => a.AvailabilityStatus);

            // --- Stats (use DB trip status for counts) ---
            int activeTrips = trips.Count(t => t.TripStatus == "Active");
            int notStarted = trips.Count(t => t.TripStatus == "Not Yet Started"
                                         || (t.TripStatus == "Pending" == false
                                             && t.TripStatus != "Active"
                                             && t.TripStatus != "Completed"
                                             && t.TripStatus != "Assignment Issue"
                                             && vehicleDict.TryGetValue(t.VehicleId, out var vNS)
                                             && vNS.VehicleStatus == "Ready to Deploy"
                                             && availabilityDict.TryGetValue(t.DriverId, out var aNS)
                                             && aNS == "Available"));
            int unassigned = trips.Count(t => t.TripStatus == "Assignment Issue"
                                         || (t.TripStatus != "Active" && t.TripStatus != "Completed"
                                             && (vehicleDict.TryGetValue(t.VehicleId, out var vU) && vU.VehicleStatus == "Flagged"
                                                 || availabilityDict.TryGetValue(t.DriverId, out var aU) && aU == "Unavailable")));
            int flaggedVehicles = vehicles.Count(v => v.VehicleStatus == "Flagged");
            int unavailableDrivers = availability.Count(a => a.AvailabilityStatus == "Unavailable");

            // --- Group trips by route → shift ---
            var vm = new DispatchViewModel
            {
                ActiveTrips = activeTrips,
                TripsNotStarted = notStarted,
                UnassignedTrips = unassigned,
                FlaggedVehicles = flaggedVehicles,
                UnavailableDrivers = unavailableDrivers
            };

            foreach (var route in routes.OrderBy(r => r.RouteId))
            {
                var routeTrips = trips.Where(t => t.RouteId == route.RouteId).ToList();

                var routeGroup = new RouteDispatchGroup
                {
                    RouteId = route.RouteId,
                    RouteName = route.RouteName,
                    NeedsAssignment = routeTrips.Any(t =>
                        t.TripStatus == "Assignment Issue" ||
                        (t.TripStatus != "Active" && t.TripStatus != "Completed" &&
                            (vehicleDict.TryGetValue(t.VehicleId, out var vR) && vR.VehicleStatus == "Flagged" ||
                             availabilityDict.TryGetValue(t.DriverId, out var aR) && aR == "Unavailable")))
                };

                if (!routeTrips.Any())
                {
                    // No trips scheduled for this route today — leave Shifts empty;
                    // the view will render an empty state for this route card.
                    vm.Routes.Add(routeGroup);
                    continue;
                }

                // Group by shift
                var shiftGroups = routeTrips
                    .GroupBy(t => new { t.ShiftType, t.ShiftStartTime, t.ShiftEndTime })
                    .OrderBy(g => g.Key.ShiftStartTime);

                foreach (var shiftGroup in shiftGroups)
                {
                    var shift = new ShiftGroup
                    {
                        ShiftType = shiftGroup.Key.ShiftType,
                        ShiftStartTime = DateTime.Today.Add(shiftGroup.Key.ShiftStartTime).ToString("h:mm tt"),
                        ShiftEndTime = DateTime.Today.Add(shiftGroup.Key.ShiftEndTime).ToString("h:mm tt")
                    };

                    foreach (var trip in shiftGroup.OrderBy(t => t.VehicleId))
                    {
                        vehicleDict.TryGetValue(trip.VehicleId, out var vehicle);
                        driverDict.TryGetValue(trip.DriverId, out var driver);

                        var driverAvail = availabilityDict.TryGetValue(trip.DriverId, out var avail)
                            ? avail : "Available";

                        // WITH this — vehicleStatus is always derived from trip context, never raw DB:
                        string vehicleStatus;
                        string driverStatus;
                        string resolvedTripStatus;

                        if (trip.TripStatus == "Active")
                        {
                            vehicleStatus = "On Trip";
                            driverStatus = "On Trip";
                            resolvedTripStatus = "Active";
                        }
                        else if (trip.TripStatus == "Completed")
                        {
                            vehicleStatus = "Ready to Deploy";
                            driverStatus = "Available";
                            resolvedTripStatus = "Completed";
                        }
                        else if (vehicle?.VehicleStatus == "Flagged" || driverAvail == "Unavailable" || driver == null)
                        {
                            // Flagged bus or unavailable/missing driver → assignment issue
                            // Keep actual statuses so dot colors reflect reality
                            vehicleStatus = vehicle?.VehicleStatus ?? "—";
                            driverStatus = driver == null ? "Unavailable" : driverAvail;
                            resolvedTripStatus = "Assignment Issue";
                        }
                        else if (vehicle?.VehicleStatus == "Pending")
                        {
                            // Bus checklist not yet passed → trip pending
                            vehicleStatus = "Pending";
                            driverStatus = driver == null ? "Unavailable" : driverAvail;
                            resolvedTripStatus = "Pending";
                        }
                        else
                        {
                            // Bus is Ready to Deploy (regardless of what other trips use it)
                            // Driver is Available → Not Yet Started
                            vehicleStatus = "Ready to Deploy";
                            driverStatus = "Available";
                            resolvedTripStatus = "Not Yet Started";
                        }

                        shift.Trips.Add(new TripRow
                        {
                            TripId = trip.TripId,
                            VehicleId = trip.VehicleId,
                            PlateNumber = vehicle?.PlateNumber ?? "—",
                            VehicleStatus = vehicleStatus,
                            DriverName = driver != null
                                ? $"{driver.FirstName} {driver.LastName}"
                                : "Unassigned",
                            DriverStatus = driverStatus,
                            TripStatus = resolvedTripStatus
                        });
                    }

                    routeGroup.Shifts.Add(shift);
                }

                vm.Routes.Add(routeGroup);
            }

            return View(vm);
        }

        // ── Update driver availability ────────────────────────────────
        [HttpPost]
        public async Task<IActionResult> UpdateDriverAvailability(int userId, string status)
        {
            if (status != "Available" && status != "Unavailable")
                return BadRequest("Invalid status.");

            var existing = await _supabase
                .From<DriverAvailability>()
                .Filter("user_id", Operator.Equals, userId.ToString())
                .Single();

            if (existing != null)
            {
                existing.AvailabilityStatus = status;
                existing.UpdatedAt = DateTime.UtcNow;
                await _supabase.From<DriverAvailability>().Upsert(existing);
                await SyncTripStatuses();
            }
            else
            {
                await _supabase.From<DriverAvailability>().Insert(new DriverAvailability
                {
                    UserId = userId,
                    AvailabilityStatus = status,
                    UpdatedAt = DateTime.UtcNow
                });
            }

            return Ok();
        }

        private async Task SyncTripStatuses(string date = null)
        {
            date ??= DateTime.Today.ToString("yyyy-MM-dd");

            var tripsTask = _supabase.From<Trip>()
                                       .Filter("date", Operator.Equals, date)
                                       .Get();
            var vehiclesTask = _supabase.From<Vehicle>().Get();
            var availabilityTask = _supabase.From<DriverAvailability>().Get();

            await Task.WhenAll(tripsTask, vehiclesTask, availabilityTask);

            var trips = tripsTask.Result.Models;
            var vehicleDict = vehiclesTask.Result.Models.ToDictionary(v => v.VehicleId);
            var availabilityDict = availabilityTask.Result.Models
                                    .ToDictionary(a => a.UserId, a => a.AvailabilityStatus);

            foreach (var trip in trips)
            {
                if (trip.TripStatus == "Active" || trip.TripStatus == "Completed")
                    continue;

                vehicleDict.TryGetValue(trip.VehicleId, out var vehicle);
                availabilityDict.TryGetValue(trip.DriverId, out var driverAvail);

                string newStatus;

                if (vehicle?.VehicleStatus == "Flagged" || driverAvail == "Unavailable")
                    newStatus = "Assignment Issue";
                else if (vehicle?.VehicleStatus == "Pending")
                    newStatus = "Pending";
                else if (vehicle?.VehicleStatus == "Ready to Deploy" && driverAvail == "Available")
                    newStatus = "Not Yet Started";
                else
                    continue;

                if (trip.TripStatus != newStatus)
                {
                    trip.TripStatus = newStatus;
                    await _supabase.From<Trip>().Upsert(trip);
                }
            }
        }
    }


}