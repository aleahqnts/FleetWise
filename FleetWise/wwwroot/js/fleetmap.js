(function () {
    // BGC, Taguig — default center used until route bounds are available
    var DEFAULT_CENTER = [14.5508, 121.0509];
    var DEFAULT_ZOOM = 16;
    var POLL_INTERVAL_MS = 5000;

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
    var busLayer = L.layerGroup().addTo(map);

    // vehicleId -> live Leaflet marker; markers are moved in place between polls,
    // never recreated, so open tooltips don't flicker (Step 11.2).
    var busMarkers = {};

    var routeSelect = document.getElementById('fmRouteFilter');
    var statusSelect = document.getElementById('fmStatusFilter');
    var connBadge = document.getElementById('fmConnBadge');

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
                    '<span class="fm-tooltip__bus">' + bus.vehicleId + '</span>' +
                    '<span class="fm-tooltip__route">' + bus.routeName + '</span>' +
                '</div>' +
                '<div class="fm-tooltip__status"><span class="fm-tooltip__dot"></span>' + bus.status + '</div>' +
                '<div class="fm-tooltip__passengers"><span>Total Passengers</span><strong>' + bus.passengers + '</strong></div>' +
            '</div>';
    }

    function setConnectionLost(lost) {
        if (connBadge) connBadge.classList.toggle('fm-conn-badge--visible', lost);
    }

    // Poll the live Positions endpoint, honouring the current Route/Status filters.
    function fetchPositions() {
        var routeId = parseInt(routeSelect.value) || null;
        var status = statusSelect.value || null;

        var params = [];
        if (routeId) params.push('routeId=' + routeId);
        if (status) params.push('status=' + encodeURIComponent(status));
        var url = '/FleetMap/Positions' + (params.length ? '?' + params.join('&') : '');

        fetch(url)
            .then(function (r) {
                if (!r.ok) throw new Error('HTTP ' + r.status);
                return r.json();
            })
            .then(function (buses) {
                setConnectionLost(false);

                var seen = {};
                buses.forEach(function (bus) {
                    seen[bus.vehicleId] = true;
                    var color = ROUTE_COLORS[bus.routeName] || '#666';
                    var marker = busMarkers[bus.vehicleId];

                    if (marker) {
                        // Move in place + refresh the (live) tooltip numbers.
                        marker.setLatLng([bus.lat, bus.lng]);
                        marker.setTooltipContent(tooltipHtml(bus));
                    } else {
                        marker = L.marker([bus.lat, bus.lng], { icon: busIcon(bus.vehicleId, color) })
                            .bindTooltip(tooltipHtml(bus), { direction: 'top', offset: [0, -10], className: 'fm-tooltip-wrap' })
                            .addTo(busLayer);
                        busMarkers[bus.vehicleId] = marker;
                    }
                });

                // Drop buses that fell out of the response (trip ended or filtered out).
                Object.keys(busMarkers).forEach(function (id) {
                    if (!seen[id]) {
                        busLayer.removeLayer(busMarkers[id]);
                        delete busMarkers[id];
                    }
                });
            })
            .catch(function (err) {
                console.error('Failed to load positions:', err);
                setConnectionLost(true); // keep markers as-is and keep polling
            });
    }

    // Fetch and render route polylines
    function loadRoutes() {
        fetch('/FleetMap/Routes')
            .then(r => r.json())
            .then(routes => {
                routes.forEach(route => {
                    if (route.waypointsJson) {
                        try {
                            var waypoints = JSON.parse(route.waypointsJson);
                            var latLngs = waypoints.map(w => [w.lat, w.lng]);
                            var color = ROUTE_COLORS[route.routeName] || '#666';

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

    // Shared filter handler — narrows polylines, stops, and live buses together
    function refetch() {
        var routeId = parseInt(routeSelect.value) || null;
        applyRouteFilter(routeId);
        loadStops(routeId);
        fetchPositions();
    }

    routeSelect.addEventListener('change', refetch);
    statusSelect.addEventListener('change', refetch);

    loadRoutes();
    loadStops(null);

    // Start live polling
    fetchPositions();
    setInterval(fetchPositions, POLL_INTERVAL_MS);
})();
