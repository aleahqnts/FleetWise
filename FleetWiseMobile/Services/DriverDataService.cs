using FleetWiseMobile.Models;
using static Postgrest.Constants;

namespace FleetWiseMobile.Services;

// Supabase reads/writes the driver app needs. Mirrors the patterns used by the
// web DispatchController (same tables, same status strings).
public class DriverDataService
{
    private readonly Supabase.Client _supabase;

    public DriverDataService(Supabase.Client supabase) => _supabase = supabase;

    public async Task<string> GetAvailabilityAsync(int userId)
    {
        var r = await _supabase.From<DriverAvailability>()
            .Filter("user_id", Operator.Equals, userId.ToString())
            .Get();
        return r.Models.FirstOrDefault()?.AvailabilityStatus ?? "Unavailable";
    }

    public async Task SetAvailabilityAsync(int userId, string status)
    {
        var existing = await _supabase.From<DriverAvailability>()
            .Filter("user_id", Operator.Equals, userId.ToString())
            .Get();
        var row = existing.Models.FirstOrDefault();

        if (row is not null)
        {
            row.AvailabilityStatus = status;
            row.UpdatedAt = PhTime.Now;
            await _supabase.From<DriverAvailability>().Upsert(row);
        }
        else
        {
            await _supabase.From<DriverAvailability>().Insert(new DriverAvailability
            {
                UserId = userId,
                AvailabilityStatus = status,
                UpdatedAt = PhTime.Now
            });
        }
    }

    // Today's assignment for this driver (anything not yet completed).
    public async Task<Trip?> GetTodayAssignmentAsync(int userId)
    {
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        var r = await _supabase.From<Trip>()
            .Filter("driver_id", Operator.Equals, userId.ToString())
            .Filter("date", Operator.Equals, today)
            .Get();

        return r.Models
            .Where(t => t.TripStatus != "Completed")
            .OrderBy(t => t.ShiftStartTime)
            .FirstOrDefault();
    }

    public async Task<BusRoute?> GetRouteAsync(int routeId)
    {
        var r = await _supabase.From<BusRoute>()
            .Filter("route_id", Operator.Equals, routeId.ToString())
            .Get();
        return r.Models.FirstOrDefault();
    }

    public async Task<Vehicle?> GetVehicleAsync(string vehicleId)
    {
        var r = await _supabase.From<Vehicle>()
            .Filter("vehicle_id", Operator.Equals, vehicleId)
            .Get();
        return r.Models.FirstOrDefault();
    }

    // Latest checklist submitted for a trip (null = none yet).
    public async Task<BusChecklist?> GetChecklistAsync(string tripId)
    {
        var r = await _supabase.From<BusChecklist>()
            .Filter("trip_id", Operator.Equals, tripId)
            .Get();
        return r.Models.OrderByDescending(c => c.SubmittedAt).FirstOrDefault();
    }

    public async Task<Trip?> GetLastCompletedTripAsync(int userId)
    {
        var r = await _supabase.From<Trip>()
            .Filter("driver_id", Operator.Equals, userId.ToString())
            .Filter("trip_status", Operator.Equals, "Completed")
            .Get();
        return r.Models.OrderByDescending(t => t.Date).FirstOrDefault();
    }

    public async Task<Trip?> GetTripAsync(string tripId)
    {
        var r = await _supabase.From<Trip>()
            .Filter("trip_id", Operator.Equals, tripId)
            .Get();
        return r.Models.FirstOrDefault();
    }

    public async Task<List<Trip>> GetTripsForDriverAsync(int userId)
    {
        var r = await _supabase.From<Trip>()
            .Filter("driver_id", Operator.Equals, userId.ToString())
            .Order("date", Ordering.Descending)
            .Get();
        return r.Models;
    }

    public async Task SubmitChecklistAsync(BusChecklist checklist)
    {
        await _supabase.From<BusChecklist>().Insert(checklist);
    }

    public async Task UpdateVehicleStatusAsync(string vehicleId, string status)
    {
        await _supabase.From<Vehicle>()
            .Where(x => x.VehicleId == vehicleId)
            .Set(x => x.VehicleStatus, status)
            .Set(x => x.UpdatedAt, (DateTime?)PhTime.Now)
            .Update();
    }

    public async Task<decimal> GetFareAsync()
    {
        var r = await _supabase.From<FareConfig>()
            .Filter("id", Operator.Equals, "1")
            .Get();
        return r.Models.FirstOrDefault()?.StandardFare ?? 0m;
    }

    // All trip writes are COLUMN-TARGETED (Set) so we never rewrite untouched
    // columns like `date` (full-model .Update round-trips the date and corrupts it).
    public async Task StartTripAsync(string tripId)
    {
        var t = await GetTripAsync(tripId);
        if (t is null) return;

        var q = _supabase.From<Trip>()
            .Where(x => x.TripId == tripId)
            .Set(x => x.TripStatus, "Active");

        // Fresh start stamps PH-now; resume keeps the original.
        if (t.TripStatus != "Active")
            q = q.Set(x => x.ActualStartTime, (DateTime?)PhTime.Now);

        await q.Update();
    }

    public async Task UpdateTripProgressAsync(string tripId, int totalBoarded, decimal revenue)
    {
        await _supabase.From<Trip>()
            .Where(x => x.TripId == tripId)
            .Set(x => x.TotalBoarded, totalBoarded)
            .Set(x => x.EstimatedRevenue, revenue)
            .Update();
    }

    public async Task EndTripAsync(string tripId, int totalBoarded, decimal revenue)
    {
        await _supabase.From<Trip>()
            .Where(x => x.TripId == tripId)
            .Set(x => x.TripStatus, "Completed")
            .Set(x => x.TotalBoarded, totalBoarded)
            .Set(x => x.EstimatedRevenue, revenue)
            .Set(x => x.ActualEndTime, (DateTime?)PhTime.Now)
            .Update();
    }
}
