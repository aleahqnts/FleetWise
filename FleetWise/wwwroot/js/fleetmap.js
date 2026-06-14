(function () {
    // BGC, Taguig — placeholder center until Block 5's route-geometry SQL step provides real bounds
    var DEFAULT_CENTER = [14.5508, 121.0509];
    var DEFAULT_ZOOM = 16;

    var map = L.map('fleetMap', {
        center: DEFAULT_CENTER,
        zoom: DEFAULT_ZOOM
    });

    L.tileLayer('https://{s}.tile-cyclosm.openstreetmap.fr/cyclosm/{z}/{x}/{y}.png', {
        maxZoom: 20,
        subdomains: ['a', 'b', 'c'],
        attribution: '<a href="https://github.com/cyclosm/cyclosm-cartocss-style/releases" title="CyclOSM - Open Bicycle render">CyclOSM</a> | ' +
            'Map data: &copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap contributors</a>'
    }).addTo(map);

    var ROUTE_COLORS = {
        'Route 01': '#2563EB',
        'Route 02': '#F97316',
        'Route 03': '#14B8A6'
    };

    function busIcon(label, color) {
        return L.divIcon({
            className: 'fm-bus-marker',
            html: '<span style="background:' + color + '">' + label + '</span>',
            iconSize: [80, 28],
            iconAnchor: [40, 14]
        });
    }

    function tooltipHtml(bus) {
        return '<div class="fm-tooltip">' +
                '<div class="fm-tooltip__header">' +
                    '<span class="fm-tooltip__bus">' + bus.label + '</span>' +
                    '<span class="fm-tooltip__route">' + bus.route + '</span>' +
                '</div>' +
                '<div class="fm-tooltip__status"><span class="fm-tooltip__dot"></span>' + bus.status + '</div>' +
                '<div class="fm-tooltip__passengers"><span>Total Passengers</span><strong>' + bus.passengers + '</strong></div>' +
            '</div>';
    }

    // Placeholder fleet — Block 11 replaces this with live data from /FleetMap/Positions
    var placeholderBuses = [
        { label: 'BUS 01', route: 'Route 01', lat: 14.5508, lng: 121.0509, status: 'Active', passengers: 11 },
        { label: 'BUS 02', route: 'Route 02', lat: 14.5538, lng: 121.0485, status: 'Active', passengers: 18 },
        { label: 'BUS 03', route: 'Route 03', lat: 14.5478, lng: 121.0548, status: 'Active', passengers: 9 },
        { label: 'BUS 04', route: 'Route 01', lat: 14.5483, lng: 121.0468, status: 'Active', passengers: 14 },
        { label: 'BUS 05', route: 'Route 03', lat: 14.5524, lng: 121.0540, status: 'Active', passengers: 22 }
    ];

    placeholderBuses.forEach(function (bus) {
        L.marker([bus.lat, bus.lng], { icon: busIcon(bus.label, ROUTE_COLORS[bus.route]) })
            .addTo(map)
            .bindTooltip(tooltipHtml(bus), { direction: 'top', offset: [0, -10], className: 'fm-tooltip-wrap' });
    });
})();
