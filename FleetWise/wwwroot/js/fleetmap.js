(function () {
    // BGC, Taguig — default center used until route bounds are available
    var DEFAULT_CENTER = [14.5508, 121.0509];
    var DEFAULT_ZOOM = 16;

    var map = L.map('fleetMap');

    L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
        maxZoom: 19,
        attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap contributors</a>',
        className: 'map-tiles'
    }).addTo(map);

    // window.fleetMapBounds = [south, west, north, east] computed from route waypoints;
    // falls back to the default center/zoom when no bounds are available
    if (window.fleetMapBounds) {
        var b = window.fleetMapBounds;
        map.fitBounds([[b[0], b[1]], [b[2], b[3]]], { padding: [60, 60], maxZoom: 16 });
    } else {
        map.setView(DEFAULT_CENTER, DEFAULT_ZOOM);
    }

    var ROUTE_COLORS = {
        'Route 01 – North Express': '#2563EB',
        'Route 02 – South Line': '#F97316'
    };

    var routePolylines = {};
    var stopLayer = L.layerGroup().addTo(map);

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

    // Static placeholder buses rendered on the map
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

    // Fetch and render route polylines
    function loadRoutes() {
        var routeColors = {
            'Route 01 – North Express': '#2563EB',
            'Route 02 – South Line': '#F97316'
        };

        fetch('/FleetMap/Routes')
            .then(r => r.json())
            .then(routes => {
                routes.forEach(route => {
                    if (route.waypointsJson) {
                        try {
                            var waypoints = JSON.parse(route.waypointsJson);
                            var latLngs = waypoints.map(w => [w.lat, w.lng]);
                            var color = routeColors[route.routeName] || '#666';

                            var polyline = L.polyline(latLngs, {
                                color: color,
                                weight: 5,
                                opacity: 0.8,
                                lineCap: 'round',
                                lineJoin: 'round'
                            }).addTo(map);

                            if (!routePolylines[route.routeId]) {
                                routePolylines[route.routeId] = [];
                            }
                            routePolylines[route.routeId].push(polyline);
                        } catch (e) {
                            console.error('Error parsing waypoints for route', route.routeName, e);
                        }
                    }
                });
            })
            .catch(err => console.error('Failed to load routes:', err));
    }

    // Re-fetch stops, optionally narrowed to one route
    function loadStops(routeId) {
        stopLayer.clearLayers();
        var url = '/FleetMap/Stops' + (routeId ? '?routeId=' + routeId : '');

        fetch(url)
            .then(response => response.json())
            .then(stops => {
                stops.forEach(function (stop) {
                    var routeColor = ROUTE_COLORS[stop.routeName] || '#999';
                    var stopIcon = L.divIcon({
                        className: 'fm-stop-marker',
                        html: '<div class="fm-stop-dot" style="background:' + routeColor + '"></div>',
                        iconSize: [24, 24],
                        iconAnchor: [12, 12]
                    });

                    L.marker([stop.lat, stop.lng], { icon: stopIcon })
                        .addTo(stopLayer)
                        .bindTooltip(stop.name, { direction: 'top', offset: [0, -10], className: 'fm-stop-tooltip' });
                });
            })
            .catch(err => console.error('Failed to load stops:', err));
    }

    // Show only the selected route's polylines (null = all)
    function applyRouteFilter(routeId) {
        Object.entries(routePolylines).forEach(([rid, polylines]) => {
            var show = !routeId || parseInt(rid) === routeId;
            polylines.forEach(line => {
                if (show && !map.hasLayer(line)) {
                    line.addTo(map);
                } else if (!show && map.hasLayer(line)) {
                    map.removeLayer(line);
                }
            });
        });
    }

    var routeSelect = document.getElementById('fmRouteFilter');
    var statusSelect = document.getElementById('fmStatusFilter');

    // Load and populate route dropdown
    fetch('/FleetMap/Routes')
        .then(r => r.json())
        .then(routes => {
            routes.forEach(route => {
                var option = document.createElement('option');
                option.value = route.routeId;
                option.textContent = route.routeName;
                routeSelect.appendChild(option);
            });
        });

    // Shared filter handler — reads both dropdowns and re-renders the route lines and stops
    function refetch() {
        var routeId = parseInt(routeSelect.value) || null;
        applyRouteFilter(routeId);
        loadStops(routeId);
    }

    routeSelect.addEventListener('change', refetch);
    statusSelect.addEventListener('change', refetch);

    loadRoutes();
    loadStops(null);
})();
