// Sample data for the browser preview, shaped like the game's bindings (names as in TrafficUISystem.cs).
// Places and numbers are based on the test city; not live data.
(function () {
    const ashbourne = {
        id: "100:1", place: "Blackthorn Highway / Ashford Highway / Oakgrove Highway", kind: "junction",
        queuesOn: [{ n: "Ashbourne Street", id: "100:1", p: -1 }, { n: "Blackthorn Highway", id: "600:1", p: -1 }], cause: "slow traffic", mix: "slow traffic 92%, give way or stop sign 8%",
        queued: 96.4, wait: 71.2, spill: 12, through: 0, hoursIn: 8, avgWait: 20.1, despawns: 6.2, trucks: 88, status: "better", since: 0, recent: 30,
        endsAt: "Blackthorn Highway / Ashford Highway / Oakgrove Highway", endsAtId: "200:1",
        moves: "Queued cars on Ashbourne Street want to turn left (96%), go straight (4%).",
        lanes: "2 lanes reach the junction (3 lanes on the road merge into 2 before it). From the left as the driver sees it: lane 1 can turn left or go straight; lane 2 can go straight.",
        flow: "Last game hour, per lane: 310 cars could turn left, 540 cars could go straight, 120 cars could turn right.",
        advice: "Turning is slow here: about 310 cars per lane turn left each game hour, against 540 going straight. Try: let a second lane turn left, make the turn wider, or give it a free-flowing slip lane.||Most queued cars on Ashbourne Street (88%) wait in one lane, even though other lanes can make the same turn. Try: a longer road before the junction so drivers change lanes earlier, or let other lanes make the turn (Traffic mod).",
        to: "Sycamore Street 39%, Spring Street 20%, Strawberry Street 6%", from: "Cargo Transfer Station 86%, Ashbourne Street 14%",
        toList: [{ n: "Sycamore Street", id: "301:1", p: 39 }, { n: "Spring Street", id: "302:1", p: 20 }, { n: "Strawberry Street", id: "303:1", p: 6 }],
        fromList: [{ n: "Cargo Transfer Station", id: "401:1", p: 86 }, { n: "Ashbourne Street", id: "100:1", p: 14 }],
    };
    const northgate = {
        id: "1201248:3", place: "Northgate Street / Walnut Street", kind: "junction", cause: "people crossing at a crosswalk", mix: "people crossing at a crosswalk 64%, traffic lights 30%",
        queued: 49.7, wait: 12.3, spill: 100, through: 20, hoursIn: 19, avgWait: 9.4, status: "worse", since: 0, recent: 16,
        moves: "Queued cars on Northgate Street want to turn left (78%), go straight (22%).",
        lanes: "3 lanes arrive at the junction: 3 can turn left, 3 can go straight, 2 can make a U-turn.",
        advice: "Cars wait at traffic lights. Try: a roundabout, removing the lights, or a longer green for this road (Traffic mod).",
        to: "Northgate Street 28%, outside the city 19%, Lilac Street 16%", from: "outside the city 19%, Dale Highway 12%",
        toList: [{ n: "Northgate Street", id: "501:1", p: 28 }, { n: "outside the city", id: "", p: 19 }, { n: "Lilac Street", id: "502:1", p: 16 }],
        fromList: [{ n: "outside the city", id: "", p: 19 }, { n: "Dale Highway", id: "503:1", p: 12 }],
    };
    const blackthorn = {
        id: "600:1", place: "Blackthorn Highway", kind: "street", cause: "road narrows or lanes end", mix: "road narrows or lanes end 100%",
        queued: 12, wait: 9.1, spill: 0, through: 0, hoursIn: 22, avgWait: 8.8, to: "", from: "", toList: [], fromList: [],
        endsAt: "Blackthorn Highway / Ashford Highway / Oakgrove Highway", endsAtId: "200:1",
        advice: "The road has fewer lanes after this point. Try: keep the same number of lanes, or upgrade the next road.",
    };
    const turn = {
        a: "Kingsbury Street / Elk Street", b: "Ashford Street", aId: "5:1", bId: "6:1", key: "5:1-6:1", gap: 280, saving: 1299, routes: 45,
        type: "turn", at: "Kingsbury Street / Elk Street", forId: "1201248:3", forName: "Northgate Street / Walnut Street", from: "Elk Street", to: "Ashford Street", via: "Kingsbury Street and Sycamore Street",
        hoursIn: 14, total: 610,
        streets: [{ n: "Elk Street", id: "701:1" }, { n: "Kingsbury Street / Elk Street", id: "5:1" }, { n: "Kingsbury Street", id: "702:1" }, { n: "Sycamore Street", id: "703:1" }, { n: "Ashford Street", id: "704:1" }],
    };
    const road = {
        a: "Ashbourne Street", b: "Seabreeze Highway", aId: "1:1", bId: "2:1", key: "1:1-2:1", gap: 372, saving: 980, routes: 163,
        type: "link", at: "Ashbourne Street", ramp: 1, forId: "100:1", forName: "Blackthorn Highway / Ashford Highway / Oakgrove Highway", from: "Ashbourne Street", to: "Seabreeze Highway", via: "Blackthorn Highway and Oakgrove Highway",
        hoursIn: 23, total: 3120,
        streets: [{ n: "Ashbourne Street", id: "100:1" }, { n: "Blackthorn Highway", id: "600:1" }, { n: "Seabreeze Highway", id: "2:1" }],
    };
    window.__previewData = {
        enabled: true,
        modActive: true,
        overlayShown: "100:1",
        bottlenecks: JSON.stringify({
            time: "2029-01-12 18:00", samples: 11, quiet: 0, window: 24, minHours: 6,
            ongoing: [northgate, ashbourne],
            ongoingLinks: [road, turn],
            rows: [ashbourne, blackthorn, northgate],
            links: [turn],
        }),
    };
})();
