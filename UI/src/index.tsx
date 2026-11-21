import { ModRegistrar } from "cs2/modding";
import { LiveTrafficPanel } from "mods/LiveTrafficPanel";

// Live Traffic Routing: statistics icon + panel in the top-left of the game screen.
const register: ModRegistrar = (moduleRegistry) => {
    moduleRegistry.append("GameTopLeft", LiveTrafficPanel);
};

export default register;
