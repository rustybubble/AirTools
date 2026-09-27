"""Curated catalog tables: product categories, the environments that list them, and the words
that point at an environment (server/catalog.py uses them).

- `CATEGORIES`: one entry per product category, shared across environments (gutters show on a
  roof and on a facade). `query` is what the catalog searches when a category has too few items;
  `aliases` are other search queries whose cached results belong here (the demo cache already
  holds "counter depth fridge", "cabinet knob", ...); `keywords` / `excludes` classify a part by
  its name or its search query (`classify`: the longest keyword wins, an exclude word vetoes).
  `icon` is a Phosphor 2.1 icon name (the app's icon font, Assets/AirTools/UI/Fonts/
  Phosphor-*.ttf); `ICON_CODEPOINTS` has their code points.
- `ENVIRONMENTS`: the kinds of place a scan can be, each with a title, how Grok is told about
  it, and its categories in display order.
- `NAME_WORDS` / `THING_WORDS`: evidence for an environment -- a word in the site name, a scene
  part or label ("dishwasher" -> kitchen).
"""

import re
from dataclasses import dataclass

from server.cache import normalize


@dataclass(frozen=True)
class Category:
    id: str
    title: str
    icon: str
    query: str
    keywords: tuple[str, ...]
    excludes: tuple[str, ...] = ()
    aliases: tuple[str, ...] = ()
    extra: bool = False  # a site-specific category Grok added (not in the curated table)


def _cat(id: str, title: str, icon: str, query: str, keywords, excludes=(), aliases=()):
    return Category(id, title, icon, query, tuple(keywords), tuple(excludes), tuple(aliases))


# fmt: off
_CATEGORY_LIST = [
    # --- kitchen ---
    _cat("fridges", "Fridges", "snowflake", "fridge",
         ["fridge", "refrigerator", "freezer", "mini fridge"],
         ["water filter", "refrigerator filter", "fridge filter", "gasket", "magnet",
          "thermometer", "deodorizer", "drip pan", "ice maker kit", "water line", "light bulb",
          "replacement shelf", "door bin"],
         ["counter depth fridge", "refrigerator"]),
    _cat("dishwashers", "Dishwashers", "washing-machine", "dishwasher",
         ["dishwasher"],
         ["dishwasher rack", "detergent", "pods", "silverware basket", "cutlery basket",
          "drain hose", "installation kit", "install kit", "dishwasher cleaner",
          "dishwasher filter", "rinse aid", "panel kit", "mounting bracket"],
         ["countertop dishwasher", "dishwasher 24 standard size",
          "dishwasher replacement standard size", "dishwasher 24 wide 24 high"]),
    _cat("ranges", "Ranges & ovens", "oven", "30 in. electric range",
         ["range", "oven", "stove", "cooker", "wall oven"],
         ["hood", "microwave", "over-the-range", "over the range", "knob", "burner grate",
          "drip pan", "igniter", "stove cover", "range cover", "burner cover", "oven liner",
          "mitt", "toaster", "oven cleaner", "power cord", "range cord", "thermometer",
          "light bulb", "oven rack", "bake element", "replacement element"],
         ["30-inch induction range"]),
    _cat("microwaves", "Microwaves", "app-window", "over-the-range microwave",
         ["microwave"],
         ["microwave cart", "trim kit", "microwave filter", "turntable", "microwave cover",
          "mounting bracket", "replacement plate"]),
    _cat("cooktops", "Cooktops", "cooking-pot", "30 in. electric cooktop",
         ["cooktop", "hob", "rangetop"],
         ["cooktop cover", "cleaner", "scraper", "protector", "cooktop mat"]),
    _cat("range-hoods", "Range hoods", "wind", "30 in. under cabinet range hood",
         ["range hood", "vent hood", "hood", "downdraft"],
         ["hood filter", "replacement filter", "light bulb", "duct tape", "damper"]),
    _cat("sinks-faucets", "Sinks & faucets", "drop", "kitchen faucet",
         ["kitchen sink", "kitchen faucet", "sink", "faucet"],
         ["basket strainer", "sink strainer", "drain stopper", "drain assembly", "sink cabinet",
          "sink base", "aerator", "cartridge", "supply line", "sink mat", "sink caddy",
          "organizer", "sink grid", "bathroom", "lavatory", "vanity", "utility", "laundry",
          "tub"],
         ["kitchen sink"]),
    _cat("cabinet-hardware", "Cabinet hardware", "hand-grabbing", "cabinet pull",
         ["cabinet hardware", "cabinet pull", "cabinet knob", "cabinet hinge", "drawer slide",
          "drawer pull", "shelf bracket", "knob", "pull", "hinge", "drawer slides"],
         ["faucet", "refrigerator", "toilet", "shower", "garage", "exterior door", "entry door"],
         ["cabinet knob", "cabinet hinge", "cabinet door hinge", "drawer slide",
          "shelf bracket"]),
    _cat("lighting", "Lighting", "lightbulb", "under cabinet light",
         ["under cabinet light", "under-cabinet light", "led strip", "strip light",
          "puck light", "light", "lighting"],
         ["hood", "microwave", "oven", "refrigerator", "switch", "outlet", "solar", "outdoor",
          "exterior", "flood", "security", "string light", "wall pack", "pole", "high bay",
          "shop light", "ceiling", "vanity", "panel light", "troffer", "flush mount",
          "chandelier", "pendant", "landscape", "porch", "post light"],
         ["led strip light 12v waterproof under cabinet"]),
    # --- rooftop ---
    _cat("gutters", "Gutters & hangers", "cloud-rain", "gutter hanger",
         ["gutter", "gutter guard", "leaf guard", "gutter hanger"],  # downspouts: their own
         ["gutter cleaning", "gutter scoop", "gutter wand", "ladder", "cleaning tool"],
         ["gutter", "gutter downspout"]),
    _cat("hvac", "HVAC units", "fan", "central air conditioner condenser",
         ["condenser", "heat pump", "hvac", "air conditioner", "mini split", "ductless",
          "packaged unit", "rooftop unit", "air handler"],
         ["window", "portable", "air filter", "furnace filter", "replacement filter",
          "condenser cover", "ac cover", "condenser pad", "equipment pad", "thermostat",
          "line set", "wall bracket", "mounting bracket", "refrigerant", "through the wall",
          "wall sleeve"],
         ["rooftop hvac unit", "heat pump"]),
    _cat("solar-panels", "Solar panels", "solar-panel", "400W solar panel",
         ["solar panel", "solar module", "photovoltaic", "pv module", "pv panel", "solar kit"],
         ["light", "lamp", "string", "fountain", "charger", "power bank", "generator", "fan",
          "landscape", "path", "flood", "pump", "camera"]),
    _cat("chimney-caps", "Chimney caps", "fire", "chimney cap",
         ["chimney cap", "chimney", "spark arrestor", "flue cap"],
         ["brush", "sweep", "cleaning", "log", "creosote", "starter"]),
    _cat("roof-vents", "Roof vents", "wind", "roof vent",
         ["roof vent", "ridge vent", "turbine vent", "attic vent", "static vent", "roof louver",
          "whirlybird", "soffit vent", "gable vent"],
         ["dryer"]),
    _cat("skylights", "Skylights", "sun", "skylight",
         ["skylight", "roof window", "sun tunnel", "tubular skylight"],
         ["skylight blind", "skylight shade", "skylight film", "skylight cover"]),
    _cat("flashing", "Flashing", "house-line", "roof flashing",
         ["flashing", "drip edge", "step flashing", "pipe boot", "vent boot"],
         ["light", "led", "beacon", "strobe"]),
    # --- facade ---
    _cat("windows", "Windows & frames", "squares-four", "vinyl window",
         ["window", "window frame", "replacement window", "double hung", "casement",
          "sliding window"],
         ["window ac", "air conditioner", "btu", "window film", "window screen",
          "replacement screen", "screen door", "blinds", "window shade", "curtain",
          "window lock", "sash lock", "window crank", "window well", "window fan",
          "window cleaner", "squeegee", "valance", "curtain rod", "window treatment",
          "roof window", "skylight", "caulk", "insulation kit"],
         ["window frame", "window"]),
    _cat("doors", "Doors", "door", "exterior door",
         ["door", "entry door", "patio door", "storm door", "exterior door", "front door"],
         ["door hinge", "door hinges", "door knob", "doorknob", "door pull", "door handle",
          "handleset", "door lock", "deadbolt", "smart lock", "door lever", "door stop",
          "doorstop", "door sweep", "door mat", "doormat", "doorbell", "door closer", "cabinet",
          "opener", "door viewer", "peephole", "door threshold", "refrigerator", "fridge",
          "freezer", "dishwasher", "oven", "range", "microwave", "door guard", "door hanger",
          "door hook", "over the door", "over-the-door", "pivot"]),
    _cat("siding", "Siding", "rows", "vinyl siding",
         ["siding", "lap siding", "fiber cement", "cladding", "shingle siding", "soffit"],
         ["nail", "tool", "cutter", "removal", "vent"]),
    _cat("exterior-lights", "Exterior lights", "lamp", "outdoor wall light",
         ["outdoor wall light", "wall lantern", "outdoor light", "exterior light", "porch light",
          "flood light", "floodlight", "security light", "post light", "outdoor sconce",
          "wall pack"],
         ["solar panel", "string"]),
    _cat("window-ac", "Window AC units", "thermometer-cold", "window ac",
         ["window ac", "window air conditioner", "window unit", "through the wall",
          "portable air conditioner", "air conditioner", "ac unit"],
         ["replacement filter", "ac cover", "air conditioner cover", "support bracket",
          "ac bracket", "insulation panel", "side panel kit", "window seal kit"],
         ["window air conditioner"]),
    # --- canopy / pavilion ---
    _cat("shade-sails", "Shade sails", "umbrella", "shade sail",
         ["shade sail", "sun shade", "shade canopy", "shade cloth", "canopy", "sun sail"],
         ["hardware kit", "umbrella base", "bed", "lamp"]),
    _cat("benches", "Benches", "chair", "outdoor bench",
         ["bench", "park bench", "garden bench", "outdoor bench"],
         ["bench press", "workbench", "work bench", "bench grinder", "bench vise", "piano",
          "benchtop", "bench top", "weight bench"]),
    _cat("outdoor-lighting", "Outdoor lighting", "lightbulb", "outdoor string lights",
         ["outdoor lighting", "string light", "string lights", "pathway light", "path light",
          "landscape light", "bollard", "pole light", "lamp post"]),
    _cat("picnic-tables", "Picnic tables", "picnic-table", "picnic table",
         ["picnic table", "outdoor table", "patio table"],
         ["table cover", "umbrella", "tablecloth", "table cloth"]),
    _cat("roof-panels", "Roof panels", "grid-four", "polycarbonate roof panel",
         ["roof panel", "roofing panel", "polycarbonate", "corrugated panel", "metal roofing",
          "roofing sheet"],
         ["solar"]),
    _cat("planters", "Planters", "potted-plant", "large outdoor planter",
         ["planter", "flower pot", "plant pot", "raised bed"]),
    # --- gym ---
    _cat("basketball-hoops", "Basketball hoops", "basketball", "wall mounted basketball hoop",
         ["basketball hoop", "basketball goal", "backboard", "basketball system", "hoop"],
         ["toy", "mini", "over the door", "arcade"]),
    _cat("gym-flooring", "Gym flooring", "grid-four", "rubber gym flooring",
         ["gym floor", "gym flooring", "rubber flooring", "rubber tile", "interlocking",
          "sports floor", "floor mat", "horse stall mat"]),
    _cat("wall-padding", "Wall padding", "shield", "gym wall padding",
         ["wall pad", "wall padding", "gym padding", "safety padding", "gym mat", "crash mat"]),
    _cat("high-bay-lights", "High-bay lights", "lightbulb", "LED high bay light",
         ["high bay", "highbay", "warehouse light", "ufo light"]),
    _cat("exhaust-fans", "Exhaust fans", "fan", "wall mount exhaust fan",
         ["exhaust fan", "ventilation fan", "shutter fan", "wall fan", "industrial fan"],
         ["range hood"]),
    _cat("mirrors", "Mirrors", "square", "large wall mirror",
         ["mirror", "wall mirror", "medicine cabinet"],
         ["car", "mirror ball", "side mirror", "rearview", "makeup"]),
    # --- hospital room ---
    _cat("hospital-beds", "Hospital beds", "bed", "hospital bed",
         ["hospital bed", "medical bed", "home care bed"],
         ["sheet", "rail pad", "mattress cover", "overbed", "over bed", "tray"]),
    _cat("overbed-tables", "Overbed tables", "table", "overbed table",
         ["overbed table", "over bed table", "over-bed table", "bedside table"]),
    _cat("iv-poles", "IV poles", "first-aid", "iv pole",
         ["iv pole", "iv stand", "infusion stand"]),
    _cat("grab-bars", "Grab bars", "wheelchair", "grab bar",
         ["grab bar", "safety rail", "safety bar", "handrail", "hand rail"]),
    _cat("privacy-curtains", "Privacy curtains", "rectangle", "hospital privacy curtain",
         ["privacy curtain", "cubicle curtain", "curtain track", "medical curtain"]),
    _cat("panel-lights", "Ceiling panel lights", "lightbulb", "2x4 LED flat panel light",
         ["flat panel", "panel light", "troffer", "2x4 led", "2x2 led"]),
    # --- bathroom ---
    _cat("toilets", "Toilets", "toilet", "toilet",
         ["toilet", "water closet"],
         ["toilet paper", "toilet brush", "plunger", "toilet seat", "flapper", "fill valve",
          "wax ring", "tank lever", "bowl cleaner", "paper holder", "auger",
          "bidet attachment", "toilet cleaner"]),
    _cat("vanities", "Vanities", "drop-half", "bathroom vanity with sink",
         ["bathroom vanity", "vanity"],
         ["vanity stool", "vanity tray", "makeup vanity"]),
    _cat("bath-faucets", "Bath faucets", "drop", "bathroom faucet",
         ["bathroom faucet", "bathroom sink faucet", "lavatory faucet", "tub faucet",
          "bathroom sink", "bath faucet"],
         ["aerator", "cartridge", "drain"]),
    _cat("showers", "Showers", "shower", "shower head",
         ["shower", "shower head", "showerhead", "shower system", "shower door"],
         ["shower gel", "shower cap", "shower caddy", "curtain rod", "shower chair",
          "shower stool", "shower radio"]),
    _cat("bathtubs", "Bathtubs", "bathtub", "alcove bathtub",
         ["bathtub", "soaking tub", "alcove tub", "freestanding tub", "tub"],
         ["bath mat", "tub mat", "stopper", "caulk", "bath tray", "faucet", "utility",
          "laundry", "cleaner"]),
    _cat("bath-fans", "Bath fans", "fan", "bathroom exhaust fan",
         ["bathroom exhaust fan", "bath fan", "bathroom fan"]),
    _cat("vanity-lights", "Vanity lights", "lightbulb", "bathroom vanity light",
         ["vanity light", "bath light", "bathroom light", "bath bar"]),
    # --- laundry ---
    _cat("washers", "Washers", "washing-machine", "front load washer",
         ["washer", "washing machine", "washer dryer combo"],
         ["pressure washer", "lock washer", "flat washer", "fender washer", "washer hose",
          "rubber washer", "split washer", "nylon washer", "bolt", "screw", "o-ring",
          "window washer", "washer box", "washer pan"]),
    _cat("dryers", "Dryers", "thermometer-hot", "electric dryer",
         ["dryer", "clothes dryer"],
         ["hair dryer", "dryer vent", "dryer sheet", "dryer ball", "hand dryer", "boot dryer",
          "duct", "hose", "lint"]),
    _cat("dryer-vents", "Dryer vents", "wind", "dryer vent kit",
         ["dryer vent", "vent hose", "dryer duct", "lint trap"]),
    _cat("utility-sinks", "Utility sinks", "drop", "laundry utility sink",
         ["utility sink", "laundry sink", "laundry tub", "utility tub"]),
    _cat("shelving", "Shelving", "archive", "wall mounted shelving",
         ["shelving", "shelf", "shelving unit", "storage rack", "wire shelf", "shelves"],
         ["liner", "label", "shelf pin", "shelf clip", "shelf bracket", "refrigerator",
          "fridge", "oven"]),
    # --- garage ---
    _cat("garage-door-openers", "Garage door openers", "garage", "garage door opener",
         ["garage door opener", "garage opener"],
         ["remote only", "keypad only"]),
    _cat("workbenches", "Workbenches", "hammer", "garage workbench",
         ["workbench", "work bench", "work table"],
         ["bench vise"]),
    _cat("shop-lights", "Shop lights", "lightbulb", "LED shop light",
         ["shop light", "garage light", "led shop"]),
    _cat("water-heaters", "Water heaters", "flame", "40 gallon water heater",
         ["water heater", "tankless"],
         ["heating element", "water heater element", "drain pan", "water heater pan",
          "relief valve", "water heater stand", "expansion tank", "anode rod", "thermostat",
          "blanket", "vent kit"]),
    # --- rooms ---
    _cat("ceiling-fans", "Ceiling fans", "fan", "ceiling fan with light",
         ["ceiling fan"],
         ["remote", "fan blade", "pull chain", "downrod", "light kit", "wall switch"]),
    _cat("ceiling-lights", "Ceiling lights", "lightbulb", "flush mount ceiling light",
         ["ceiling light", "flush mount", "semi flush", "chandelier", "pendant light",
          "recessed light", "can light"],
         ["ceiling fan"]),
    _cat("blinds", "Blinds & shades", "square-split-vertical", "cordless blinds",
         ["blind", "roller shade", "cellular shade", "window shade", "blinds"],
         ["shade sail", "sun shade", "lamp shade"]),
    _cat("smoke-detectors", "Smoke detectors", "siren", "smoke detector",
         ["smoke detector", "smoke alarm", "carbon monoxide", "co detector"]),
    _cat("outlets-switches", "Outlets & switches", "plug", "gfci outlet",
         ["outlet", "receptacle", "light switch", "dimmer switch", "wall plate", "gfci",
          "dimmer"],
         ["power strip", "surge", "timer", "extension", "outdoor outlet cover", "smart plug"]),
    # --- gaze focus (what the wearer looks at: FOCUS_TABLES below) ---
    _cat("wall-hvac", "Wall-mounted HVAC", "thermometer-cold", "ductless mini split air conditioner",
         ["mini split", "ductless mini split", "ductless", "ptac", "packaged terminal",
          "wall mounted air conditioner", "wall air conditioner", "through the wall air conditioner",
          "wall mounted hvac"],
         ["line set", "wall bracket", "mounting bracket", "cover", "filter", "refrigerant",
          "remote", "condenser pad"]),
    _cat("wall-vents", "Wall vents", "wind", "exterior wall vent",
         ["wall vent", "exterior vent", "louvered vent", "louver vent", "wall cap", "vent hood",
          "exhaust hood"],
         ["roof", "range hood", "dryer vent kit", "cleaning", "brush"]),
    _cat("downspouts", "Downspouts", "cloud-rain", "vinyl downspout",
         ["downspout", "downspout extension", "rain diverter", "downspout elbow"],
         ["cleaning", "scoop", "wand"]),
    _cat("snow-guards", "Snow guards", "snowflake", "roof snow guard",
         ["snow guard", "snow stop", "snow bar", "snow retention"]),
    _cat("satellite-mounts", "Satellite mounts", "house-line", "satellite dish roof mount",
         ["satellite mount", "dish mount", "antenna mount", "satellite dish mount", "mast mount"],
         ["tv wall mount", "tv mount", "monitor"]),
    _cat("pavers", "Pavers", "grid-four", "concrete patio paver",
         ["paver", "patio stone", "stepping stone", "patio block", "flagstone"],
         ["sand", "sealer", "edging", "tool", "base panel"]),
    _cat("drainage", "Drainage", "drop", "channel drain",
         ["channel drain", "trench drain", "catch basin", "drain grate", "french drain",
          "yard drain", "drainage"],
         ["sink", "tub", "shower", "floor drain cover", "hair", "snake", "auger"]),
    _cat("backsplash", "Backsplash tile", "squares-four", "kitchen backsplash tile",
         ["backsplash", "peel and stick tile", "subway tile", "mosaic tile", "wall tile"],
         ["adhesive", "grout", "trowel", "cutter", "spacer"]),
]
# fmt: on

CATEGORIES: dict[str, Category] = {c.id: c for c in _CATEGORY_LIST}


@dataclass(frozen=True)
class Environment:
    id: str
    title: str
    about: str  # how Grok is told which places this covers
    categories: tuple[str, ...]


# fmt: off
ENVIRONMENTS: dict[str, Environment] = {e.id: e for e in [
    Environment("kitchen", "Kitchen", "a home or office kitchen or kitchenette",
                ("fridges", "dishwashers", "ranges", "microwaves", "cooktops", "range-hoods",
                 "sinks-faucets", "cabinet-hardware", "lighting")),
    Environment("bathroom", "Bathroom", "a bathroom, restroom or washroom",
                ("toilets", "vanities", "bath-faucets", "showers", "bathtubs", "bath-fans",
                 "mirrors", "vanity-lights", "grab-bars")),
    Environment("laundry", "Laundry room", "a laundry or utility room",
                ("washers", "dryers", "dryer-vents", "utility-sinks", "shelving")),
    Environment("garage", "Garage", "a garage or workshop",
                ("garage-door-openers", "shelving", "workbenches", "shop-lights",
                 "water-heaters")),
    Environment("interior", "Room", "a living room, bedroom, office or hallway",
                ("ceiling-fans", "ceiling-lights", "window-ac", "blinds", "smoke-detectors",
                 "outlets-switches")),
    Environment("gym", "Gym", "an indoor gym, fitness room or sports hall",
                ("basketball-hoops", "gym-flooring", "wall-padding", "high-bay-lights",
                 "exhaust-fans", "mirrors")),
    Environment("hospital", "Hospital room", "a hospital room, ward or clinic room",
                ("hospital-beds", "overbed-tables", "iv-poles", "grab-bars", "privacy-curtains",
                 "panel-lights")),
    Environment("rooftop", "Rooftop",
                "on or over a roof, pitched or flat; a drone view where roofs fill much of the "
                "frame",
                ("gutters", "hvac", "solar-panels", "chimney-caps", "roof-vents", "skylights",
                 "flashing")),
    Environment("facade", "Facade",
                "the outside walls of a building, seen from the street or a drone facing them",
                ("windows", "doors", "siding", "gutters", "exterior-lights", "window-ac")),
    Environment("pavilion", "Canopy & pavilion",
                "a canopy, pavilion, shelter, pergola, patio, courtyard or park",
                ("shade-sails", "benches", "outdoor-lighting", "picnic-tables", "roof-panels",
                 "planters", "gutters")),
    Environment("generic", "Anywhere", "none of these",
                ("windows", "doors", "ceiling-lights", "shelving", "smoke-detectors",
                 "outlets-switches", "window-ac")),
]}

GENERIC = "generic"

# --- gaze focus: what the wearer is looking at (the app's head ray, classified on the headset) ---
#
# The app sends `focus` with GET /catalog: the surface the head ray meets, classified in the
# scan's own up frame. Per setting (the environment's kind of place) and focus, the categories to
# lead with, in order. An environment × focus not in the table gets the unfocused list.

FOCI = ("roof", "wall", "ground", "ceiling", "counter", "opening")
FOCUS_ALIASES = {
    "floor": "ground", "grass": "ground", "walls": "wall", "facade": "wall", "roofs": "roof",
    "rooftop": "roof", "window": "opening", "windows": "opening", "door": "opening",
    "doors": "opening", "countertop": "counter", "backsplash": "counter",
}

FOCUS_TABLES: dict[str, dict[str, tuple[str, ...]]] = {
    # Outside a building: a drone scan of it (Zabel, the hospital), a facade, a roof, a pavilion.
    "exterior": {
        "roof": ("solar-panels", "gutters", "roof-vents", "hvac", "skylights", "chimney-caps",
                 "flashing", "snow-guards", "satellite-mounts"),
        "wall": ("wall-hvac", "windows", "window-ac", "doors", "siding", "exterior-lights",
                 "wall-vents", "downspouts"),
        "ground": ("pavers", "planters", "benches", "drainage", "picnic-tables",
                   "outdoor-lighting", "hvac"),
        "ceiling": ("exterior-lights", "ceiling-fans", "siding"),
        "opening": ("windows", "doors", "window-ac", "blinds"),
    },
    "kitchen": {
        "counter": ("microwaves", "sinks-faucets", "cooktops", "backsplash", "lighting",
                    "cabinet-hardware"),
        "ground": ("dishwashers", "fridges", "ranges", "cabinet-hardware"),
        "wall": ("range-hoods", "microwaves", "cabinet-hardware", "lighting", "outlets-switches",
                 "backsplash", "shelving"),
        "ceiling": ("ceiling-lights", "smoke-detectors", "range-hoods", "ceiling-fans"),
        "opening": ("windows", "blinds", "doors"),
    },
    # Any other room: its own environment's categories where they belong, and the shared ones
    # (ROOM_SHARED); another room's (a washer in a bathroom) are dropped (focus_ids).
    "room": {
        "ceiling": ("ceiling-lights", "ceiling-fans", "smoke-detectors", "panel-lights",
                    "high-bay-lights", "bath-fans"),
        "wall": ("outlets-switches", "window-ac", "mirrors", "shelving", "grab-bars",
                 "wall-padding", "basketball-hoops", "vanity-lights", "exhaust-fans"),
        "ground": ("toilets", "vanities", "bathtubs", "showers", "washers", "dryers",
                   "utility-sinks", "water-heaters", "workbenches", "hospital-beds",
                   "overbed-tables", "iv-poles", "gym-flooring", "shelving"),
        "counter": ("bath-faucets", "vanities", "utility-sinks", "workbenches", "overbed-tables"),
        "opening": ("windows", "blinds", "doors", "window-ac", "privacy-curtains"),
    },
}
ROOM_SHARED = frozenset({"ceiling-lights", "ceiling-fans", "smoke-detectors", "outlets-switches",
                         "window-ac", "shelving", "mirrors", "grab-bars", "windows", "blinds",
                         "doors"})
FOCUS_SETTING = {
    "kitchen": "kitchen",
    "rooftop": "exterior", "facade": "exterior", "pavilion": "exterior",
    "bathroom": "room", "laundry": "room", "garage": "room", "interior": "room", "gym": "room",
    "hospital": "room",
}

# Words that put one of Grok's site extras under a focus ("Red Tile Roofs" -> roof). A roof word
# wins alone (a dormer window is on the roof).
_FOCUS_WORDS: dict[str, tuple[str, ...]] = {
    "roof": ("roof", "rooftop", "roofing", "dormer", "chimney", "skylight", "solar", "gutter",
             "shingle", "ridge", "attic", "flashing", "snow guard", "satellite"),
    "wall": ("wall", "siding", "facade", "brick", "cladding", "shutter", "sconce", "downspout",
             "awning", "mural", "trim"),
    "opening": ("window", "door", "blind", "shade", "shutter"),
    "ground": ("paver", "planter", "bench", "patio", "deck", "drain", "floor", "lawn", "garden",
               "path", "fence", "bollard", "playground", "picnic", "bridge", "climbing"),
    "ceiling": ("ceiling", "pendant", "chandelier", "soffit"),
    "counter": ("countertop", "counter", "backsplash", "sink", "faucet"),
}

# Phosphor 2.1 (the app's icon font) code points for every icon the tables and Grok's extras may
# use, read from Phosphor-Regular.ttf's GSUB ligatures (the Fill font shares them).
ICON_CODEPOINTS: dict[str, str] = {
    "app-window": "E5DA", "archive": "E00C", "basketball": "E724", "bathtub": "E81E",
    "bed": "E0CC", "chair": "E950", "cloud-rain": "E1B4", "cooking-pot": "E764", "door": "E61C",
    "drop": "E210", "drop-half": "E566", "fan": "E9F2", "fire": "E242", "first-aid": "E56E",
    "flame": "E624", "garage": "ECD6", "grid-four": "E296", "hammer": "E80E",
    "hand-grabbing": "E57C", "house-line": "E2C4", "lamp": "E638", "lightbulb": "E2DC",
    "lightning": "E2DE", "oven": "ED8C", "package": "E390", "picnic-table": "EE26",
    "pipe": "ED86", "plug": "E946", "potted-plant": "EC22", "rectangle": "E3F0", "rows": "E5A2",
    "shield": "E40A", "shower": "E776", "siren": "E9B8", "snowflake": "E5AA",
    "solar-panel": "ED7A", "solar-roof": "ED7B", "square": "E45E",
    "square-split-vertical": "E874", "squares-four": "E464", "sun": "E472", "table": "E476",
    "thermometer-cold": "E5C8", "thermometer-hot": "E5CA", "toilet": "E79A", "tree": "E6DA",
    "umbrella": "E684", "washing-machine": "EDE8", "wheelchair": "E4E8", "wind": "E5D2",
    "wrench": "E5D4",
}
DEFAULT_ICON = "package"

# Words in a site's name. Place words say where you stand (1.5); building words only say what
# kind of building it is (0.75: "zabel-gymnasium" is a German school, seen from a drone).
NAME_WORDS: dict[str, tuple[float, tuple[str, ...]]] = {
    "kitchen": (1.5, ("kitchen", "kitchenette")),
    "bathroom": (1.5, ("bath", "bathroom", "restroom", "washroom")),
    "laundry": (1.5, ("laundry",)),
    "garage": (1.5, ("garage", "workshop")),
    "interior": (1.0, ("bedroom", "living", "office", "lounge", "hallway")),
    "rooftop": (1.5, ("roof", "rooftop", "attic")),
    "facade": (1.5, ("facade", "façade", "storefront", "elevation", "exterior")),
    "pavilion": (1.5, ("canopy", "pavilion", "shelter", "courtyard", "patio", "park", "pergola",
                       "plaza")),
    "gym": (0.75, ("gym", "gymnasium", "fitness")),
    "hospital": (0.75, ("hospital", "clinic", "ward")),
}

# Things in a scene (a parts-file component, a structure object, a Grok label) -> environment:
# (env, weight, words, excludes). An appliance is strong evidence, a cabinet or a window a hint;
# a "cabinet door" is no door of a facade.
THING_WORDS: list[tuple[str, float, tuple[str, ...], tuple[str, ...]]] = [
    ("kitchen", 1.5, ("dishwasher", "fridge", "refrigerator", "range", "oven", "stove",
                      "cooktop", "microwave", "range hood", "kitchen sink"), ()),
    ("kitchen", 0.5, ("cabinet", "base cabinet", "countertop", "cabinet door", "backsplash"),
     ("medicine cabinet", "vanity")),
    ("bathroom", 1.5, ("toilet", "vanity", "bathtub", "tub", "shower"), ()),
    ("laundry", 1.5, ("washer", "dryer", "washing machine"), ()),
    ("garage", 1.5, ("garage door", "workbench"), ()),
    ("rooftop", 1.0, ("gutter", "chimney", "solar panel", "hvac", "skylight", "roof vent",
                      "dormer", "roof"), ()),
    ("facade", 0.25, ("window", "door"),
     ("cabinet", "oven", "microwave", "fridge", "refrigerator", "dishwasher", "shower",
      "garage", "screen")),
    ("hospital", 1.5, ("hospital bed", "iv pole"), ()),
    ("gym", 1.5, ("basketball hoop", "bleachers", "treadmill"), ()),
    ("pavilion", 1.0, ("bench", "picnic table", "canopy"), ("workbench",)),
]
# fmt: on

# --- matching ---------------------------------------------------------------------------------


def _words_re(words) -> re.Pattern | None:
    """Any of `words` as whole words, each with an optional plural (-s, -es, y -> -ies)."""
    variants = []
    for w in words:
        if not w:
            continue
        variants.append(w)
        if len(w) > 3 and w.endswith("y") and w[-2] not in "aeiou":
            variants.append(w[:-1] + "ies")
    if not variants:
        return None
    alts = "|".join(
        r"\s*[-\s]\s*".join(re.escape(part) for part in re.split(r"[\s-]+", w))
        for w in sorted(set(variants), key=len, reverse=True)
    )
    return re.compile(rf"(?<![a-z0-9])(?:{alts})(?:e?s)?(?![a-z0-9])", re.IGNORECASE)


# fmt: off
_JUNK = _words_re(["toy", "miniature", "dollhouse", "1:12", "1/12", "1-12", "scale model",
                   "model for desktop", "decal", "sticker", "costume"])
# fmt: on
_KEYWORD_RES = {c.id: _words_re(c.keywords) for c in _CATEGORY_LIST}
_EXCLUDE_RES = {c.id: _words_re(c.excludes) for c in _CATEGORY_LIST}
_QUERY_TO_CATEGORY: dict[str, str] = {}
for _c in _CATEGORY_LIST:
    for _q in (_c.query, *_c.aliases):
        _QUERY_TO_CATEGORY.setdefault(normalize(_q), _c.id)
_THING_RES = [(env, w, _words_re(words), _words_re(ex)) for env, w, words, ex in THING_WORDS]


def normalize_query(text: str) -> str:
    """The search caches' own key form (cache.normalize), so a curated query finds its result."""
    return normalize(text)


def is_curated_query(text: str) -> bool:
    return normalize(text) in _QUERY_TO_CATEGORY


def is_junk(text: str) -> bool:
    return bool(_JUNK.search(text or ""))


# fmt: off
_NOT_EXTRAS = _words_re(["supplies", "supply", "cleaner", "cleaning", "sponge", "organizer",
                         "decor", "decoration", "soap", "towel", "utensil", "cookware", "bottle",
                         "food", "sign", "signage", "poster", "items", "accessories", "clutter",
                         "toy", "plant", "artwork", "rug", "cushion", "pillow"])
# fmt: on


def not_an_extra(text: str) -> bool:
    """A site extra Grok proposed that isn't a thing a contractor installs (the kitchen's first
    try gave "Cleaning Supplies" and "Countertop Items")."""
    return bool(_NOT_EXTRAS.search(text or "")) or is_junk(text)


def match_strength(category: Category, text: str) -> int:
    """How strongly `text` names `category`: the length of the longest keyword it contains (0:
    none, or an exclude word is in it). A Grok extra matches on its own keywords."""
    if not text:
        return 0
    if category.extra:
        kw, ex = _words_re(category.keywords), _words_re(category.excludes)
    else:
        kw, ex = _KEYWORD_RES.get(category.id), _EXCLUDE_RES.get(category.id)
    if kw is None or (ex is not None and ex.search(text)):
        return 0
    return max((len(m.group(0)) for m in kw.finditer(text)), default=0)


def classify(text: str, among: list[Category] | None = None) -> str | None:
    """The category `text` (a part name or a search query) belongs to: an exact curated query or
    alias first, else the category whose keyword it contains most specifically (the longest
    match; ties go to table order). None for junk (toys, scale models) or no match."""
    if not text or is_junk(text):
        return None
    pool = among if among is not None else _CATEGORY_LIST
    exact = _QUERY_TO_CATEGORY.get(normalize(text))
    if exact is not None and any(c.id == exact for c in pool):
        return exact
    best, best_len = None, 0
    for category in pool:
        n = match_strength(category, text)
        if n > best_len:
            best, best_len = category.id, n
    return best


def env_votes_for_thing(text: str) -> list[tuple[str, float]]:
    """(environment, weight) for one scene thing's label; the strongest entry per environment."""
    out: dict[str, float] = {}
    for env, weight, words, excludes in _THING_RES:
        if (
            words.search(text or "")
            and not (excludes and excludes.search(text or ""))
            and weight > out.get(env, 0.0)
        ):
            out[env] = weight
    return list(out.items())


def name_votes(name: str) -> list[tuple[str, float, str]]:
    """(environment, weight, word) for the words of a site name ("gt-lcc-canopy" -> pavilion)."""
    words = set(re.split(r"[^a-zà-ÿ]+", (name or "").lower()))
    out = []
    for env, (weight, env_words) in NAME_WORDS.items():
        hit = next((w for w in env_words if w in words), None)
        if hit:
            out.append((env, weight, hit))
    return out


def slug(text: str) -> str:
    return re.sub(r"[^a-z0-9]+", "-", text.lower()).strip("-")[:40] or "extra"


def extra_category(title: str, query: str, icon: str) -> Category:
    """A site-specific category Grok proposed. Its keywords are its query and the query's last
    two words (singular), so parts from that search -- and parts named like it -- land in it."""
    stop = {"and", "the", "for", "with", "inch", "outdoor", "indoor", "large", "small"}
    words = [w for w in re.findall(r"[a-z0-9]+", query.lower()) if len(w) > 2 and w not in stop]
    words = [w[:-1] if w.endswith("s") and not w.endswith("ss") else w for w in words]
    title = title.strip()[:40] or query.strip()
    return Category(
        id=f"x-{slug(title)}",
        title=title[:1].upper() + title[1:],
        icon=icon if icon in ICON_CODEPOINTS else DEFAULT_ICON,
        query=query.strip()[:80],
        keywords=tuple(dict.fromkeys([query.lower().strip(), " ".join(words[-2:])])),
        extra=True,
    )


# --- gaze focus -------------------------------------------------------------------------------

_FOCUS_RES = {f: _words_re(words) for f, words in _FOCUS_WORDS.items()}


def normalize_focus(text: str | None) -> str | None:
    """A focus the tables know ("Roof" -> "roof", "floor" -> "ground"); None for none, "all" or
    anything else (the unfocused catalog)."""
    f = (text or "").strip().lower()
    f = FOCUS_ALIASES.get(f, f)
    return f if f in FOCI else None


def focus_setting(environment: str | None, setting: str | None = None) -> str | None:
    """Which focus table an environment uses: exterior, kitchen or room. The generic place goes
    by Grok's setting (indoor -> room, outdoor / aerial -> exterior); None when unknown."""
    if environment in FOCUS_SETTING:
        return FOCUS_SETTING[environment]
    if setting == "indoor":
        return "room"
    if setting in ("outdoor", "aerial"):
        return "exterior"
    return None


def focus_ids(environment: str | None, focus: str, setting: str | None = None) -> tuple[str, ...]:
    """The curated category ids for `focus` in `environment`, in order; () when the table has
    none. A room keeps its own environment's categories and the shared ones."""
    key = focus_setting(environment, setting)
    ids = FOCUS_TABLES.get(key or "", {}).get(focus, ())
    if key == "room":
        own = set(ENVIRONMENTS[environment].categories) if environment in ENVIRONMENTS else set()
        ids = tuple(c for c in ids if c in own or c in ROOM_SHARED)
    return ids


def foci_for(environment: str | None, setting: str | None = None) -> list[str]:
    """The foci a site's catalog answers (in FOCI order)."""
    return [f for f in FOCI if focus_ids(environment, f, setting)]


def foci_of_text(text: str) -> set[str]:
    """The foci a site extra belongs to by its words ("Red Tile Roofs" -> {"roof"}); a roof word
    wins alone."""
    hits = {f for f, rx in _FOCUS_RES.items() if rx is not None and rx.search(text or "")}
    return {"roof"} if "roof" in hits else hits
