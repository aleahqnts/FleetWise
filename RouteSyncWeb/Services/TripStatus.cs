using FleetWise.Models;

namespace FleetWise.Services
{
    /// <summary>How a trip and its assignment read right now.</summary>
    public sealed record TripStatusView(
        string VehicleStatus,
        string DriverStatus,
        string TripStatus,
        bool VehicleFlagged);

    /// <summary>
    /// The single place the dispatch board, the trip detail modal and the header
    /// counters work out what a trip looks like.
    /// </summary>
    /// <remarks>
    /// Statuses are derived per request rather than stored. The vehicle_status column
    /// belongs to whichever shift wrote it last, so a stored value would show a missed
    /// trip as ready and would contradict the inspection log.
    ///
    /// A vehicle is flagged when it has an unresolved maintenance incident, and by
    /// nothing else. A failed inspection opens one of those incidents, so the fault
    /// still surfaces, and the flag clears when someone resolves it in the vehicles
    /// tab. The flag is deliberately not read from the checklist row, which no
    /// action on the dashboard can rewrite.
    ///
    /// A flag is advisory and the bus stays deployable. Only a grounded bus or an
    /// unavailable driver is an assignment issue.
    /// </remarks>
    public static class TripStatus
    {
        /// <summary>
        /// When the shift closes. A window whose end is not after its start runs past
        /// midnight and therefore closes on the following day.
        /// </summary>
        public static DateTime ShiftEndAt(Trip trip) =>
            trip.Date.Date
                .Add(trip.ShiftEndTime)
                .AddDays(trip.ShiftEndTime <= trip.ShiftStartTime ? 1 : 0);

        /// <param name="checklist">
        /// This trip's most recent inspection, or null when none was submitted.
        /// </param>
        /// <param name="vehicleFlagged">
        /// Whether the vehicle has an unresolved maintenance incident.
        /// </param>
        public static TripStatusView Resolve(
            Trip trip,
            Vehicle? vehicle,
            UserModel? driver,
            string? driverAvailability,
            BusChecklist? checklist,
            bool vehicleFlagged,
            DateTime now)
        {
            // An inspection recorded against a different bus belongs to a reassignment
            // that has already happened, so it says nothing about the bus on the trip
            // now. That bus needs its own inspection.
            if (checklist != null &&
                !string.Equals(checklist.VehicleId, trip.VehicleId, StringComparison.OrdinalIgnoreCase))
            {
                checklist = null;
            }

            if (trip.TripStatus == "Active")
                return new TripStatusView("On Trip", "On Trip", "Active", vehicleFlagged);

            if (trip.TripStatus == "Completed")
                return new TripStatusView("Completed", "Available", "Completed", vehicleFlagged);

            // Waiting to depart. Readiness comes from this trip's own inspection: none
            // yet is pending, and an open incident against the bus is a flag.
            var vehicleStatus = checklist == null ? "Pending"
                              : vehicleFlagged ? "Flagged"
                              : "Ready to Deploy";

            // A driver with no availability row counts as available.
            var driverStatus = driver == null ? "Unavailable"
                             : string.IsNullOrEmpty(driverAvailability) ? "Available"
                             : driverAvailability;

            // A shift that closed without the trip ever starting counts as missed, so a
            // past operational day shows missed trips rather than a stale "not yet
            // started".
            var tripStatus =
                ShiftEndAt(trip) < now ? "Missed"
                : (vehicle?.OutOfService == true || driverStatus == "Unavailable") ? "Assignment Issue"
                : vehicleStatus == "Pending" ? "Pending"
                : "Not Yet Started";

            return new TripStatusView(vehicleStatus, driverStatus, tripStatus, vehicleFlagged);
        }
    }
}
