namespace FleetWise.ViewModels
{
    // Returned by GET /Dispatch/GetAddTripOptions
    public class AddTripOptionsViewModel
    {
        public List<RouteOption> Routes { get; set; } = new();
        public List<VehicleOption> Vehicles { get; set; } = new();
        public List<DriverOption> Drivers { get; set; } = new();
    }

    public class RouteOption
    {
        public int RouteId { get; set; }
        public string RouteName { get; set; }
    }

    public class VehicleOption
    {
        public string VehicleId { get; set; }
        public string PlateNumber { get; set; }
        public string VehicleType { get; set; }
        // Shifts this vehicle is already booked for today
        public List<string> BookedShifts { get; set; } = new();
    }

    public class DriverOption
    {
        public int DriverId { get; set; }
        public string DriverName { get; set; }
        // Shifts this driver is already booked for today
        public List<string> BookedShifts { get; set; } = new();
    }

    // Posted by POST /Dispatch/CreateTrip
    public class CreateTripRequest
    {
        public string ShiftType { get; set; }
        public string ShiftStartTime { get; set; }  // "HH:mm"
        public string ShiftEndTime { get; set; }  // "HH:mm"
        public int RouteId { get; set; }
        public string VehicleId { get; set; }
        public int DriverId { get; set; }
        // Dispatcher acknowledged the conflict and chose to create the trip anyway.
        public bool Override { get; set; }
    }

    // Posted by POST /Dispatch/ReassignTrip
    public class ReassignTripRequest
    {
        public string TripId { get; set; }
        public string VehicleId { get; set; }   // null = keep existing
        public int? DriverId { get; set; }   // null = keep existing
        // Dispatcher acknowledged the conflict and chose to save the reassignment anyway.
        public bool Override { get; set; }
    }

    // Posted by POST /Dispatch/RemoveTrip — clearing both bus + driver in Reassign deletes
    // the trip (mirrors clearing a cell in the schedule planner).
    public class RemoveTripRequest
    {
        public string TripId { get; set; }
    }

    // Posted by POST /Dispatch/BroadcastMessage
    public class BroadcastMessageRequest
    {
        public string Subject { get; set; }
        public string Body { get; set; }
        public string Priority { get; set; }
    }

    // Posted by POST /Dispatch/SendRouteMessage
    public class RouteMessageRequest
    {
        public int RouteId { get; set; }
        public string Subject { get; set; }
        public string Body { get; set; }
        public string Priority { get; set; }
    }

    // Posted by POST /Dispatch/SendTripMessage
    public class TripMessageRequest
    {
        public string TripId { get; set; }
        public string Subject { get; set; }
        public string Body { get; set; }
        public string Priority { get; set; }
    }
}
