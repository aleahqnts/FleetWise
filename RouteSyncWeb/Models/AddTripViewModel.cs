using System.ComponentModel.DataAnnotations;

namespace FleetWise.ViewModels
{
    // Validation lives on the REQUEST models only (the ones below with a [FromBody]
    // action behind them), never on the view models above, which the server fills in.
    //
    // The forms already check these fields in the browser, which is a courtesy to the
    // person typing and no defence at all: the endpoints are plain JSON, so anyone with
    // a session can post to them directly and skip every one of those checks. These
    // attributes are the copy that actually runs.

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
        [Required, RegularExpression("^(Morning|Afternoon|Evening)$",
            ErrorMessage = "Shift must be Morning, Afternoon or Evening.")]
        public string ShiftType { get; set; }

        [Required, RegularExpression(@"^([01]?\d|2[0-3]):[0-5]\d$", ErrorMessage = "Start time must be HH:mm.")]
        public string ShiftStartTime { get; set; }  // "HH:mm"

        [Required, RegularExpression(@"^([01]?\d|2[0-3]):[0-5]\d$", ErrorMessage = "End time must be HH:mm.")]
        public string ShiftEndTime { get; set; }  // "HH:mm"

        [Range(1, int.MaxValue, ErrorMessage = "A route is required.")]
        public int RouteId { get; set; }

        [Required, RegularExpression(@"^[A-Za-z0-9-]{1,20}$", ErrorMessage = "That is not a vehicle ID.")]
        public string VehicleId { get; set; }

        [Range(1, int.MaxValue, ErrorMessage = "A driver is required.")]
        public int DriverId { get; set; }
        // Dispatcher acknowledged the conflict and chose to create the trip anyway.
        public bool Override { get; set; }
    }

    // Posted by POST /Dispatch/ReassignTrip
    public class ReassignTripRequest
    {
        [Required, RegularExpression(@"^[A-Za-z0-9_-]{1,64}$", ErrorMessage = "That is not a trip ID.")]
        public string TripId { get; set; }

        // Optional on purpose: null means "leave this one alone", and the UI clears a
        // slot by sending an empty string, so the pattern has to let both through.
        [RegularExpression(@"^([A-Za-z0-9-]{1,20})?$", ErrorMessage = "That is not a vehicle ID.")]
        public string VehicleId { get; set; }   // null = keep existing

        [Range(1, int.MaxValue, ErrorMessage = "That is not a driver.")]
        public int? DriverId { get; set; }   // null = keep existing
        // Dispatcher acknowledged the conflict and chose to save the reassignment anyway.
        public bool Override { get; set; }
    }

    // Posted by POST /Dispatch/RemoveTrip — clearing both bus + driver in Reassign deletes
    // the trip (mirrors clearing a cell in the schedule planner).
    public class RemoveTripRequest
    {
        [Required, RegularExpression(@"^[A-Za-z0-9_-]{1,64}$", ErrorMessage = "That is not a trip ID.")]
        public string TripId { get; set; }
    }

    // Posted by POST /Dispatch/BroadcastMessage
    public class BroadcastMessageRequest
    {
        [StringLength(120, ErrorMessage = "Subject is too long.")]
        public string Subject { get; set; }

        // Drivers read this on a phone. The cap is generous for a real dispatch note and
        // still stops a single post from filling the messages table.
        [Required(ErrorMessage = "Message body is required."), StringLength(2000, MinimumLength = 1)]
        public string Body { get; set; }

        [RegularExpression("^(Normal|High)$", ErrorMessage = "Priority must be Normal or High.")]
        public string Priority { get; set; }
    }

    // Posted by POST /Dispatch/SendRouteMessage
    public class RouteMessageRequest
    {
        [Range(1, int.MaxValue, ErrorMessage = "A route is required.")]
        public int RouteId { get; set; }

        [StringLength(120, ErrorMessage = "Subject is too long.")]
        public string Subject { get; set; }

        // Drivers read this on a phone. The cap is generous for a real dispatch note and
        // still stops a single post from filling the messages table.
        [Required(ErrorMessage = "Message body is required."), StringLength(2000, MinimumLength = 1)]
        public string Body { get; set; }

        [RegularExpression("^(Normal|High)$", ErrorMessage = "Priority must be Normal or High.")]
        public string Priority { get; set; }
    }

    // Posted by POST /Dispatch/SendTripMessage
    public class TripMessageRequest
    {
        [Required, RegularExpression(@"^[A-Za-z0-9_-]{1,64}$", ErrorMessage = "That is not a trip ID.")]
        public string TripId { get; set; }

        [StringLength(120, ErrorMessage = "Subject is too long.")]
        public string Subject { get; set; }

        // Drivers read this on a phone. The cap is generous for a real dispatch note and
        // still stops a single post from filling the messages table.
        [Required(ErrorMessage = "Message body is required."), StringLength(2000, MinimumLength = 1)]
        public string Body { get; set; }

        [RegularExpression("^(Normal|High)$", ErrorMessage = "Priority must be Normal or High.")]
        public string Priority { get; set; }
    }
}
