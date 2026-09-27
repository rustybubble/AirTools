# Hand-off p4-edit6dof: voice for the Edit view (backend, not done)

The headset now has these agent actions (`Runtime/Agent/PlacementActions.cs`):
- `edit_part {part?}` opens the Edit view on a placed part.
- `delete_part {part?}` deletes it (undoable).
- `view_similar {part?}` opens the Catalog on its kind.
- `adjust_placement {mode: "on" | "edit" | "toggle"}` (and a bare `adjust_placement`) opens the Edit view.

`part` is a placed part's id, name or kind ("dishwasher"). Without it, the selected part is used, else the last placed.

The backend on `demo-next` (`server/agent.py` TOOLS) declares none of them, so "edit it" and "delete the dishwasher"
don't reach the headset yet. The app lane made no backend commits; these declarations are for the backend lane to add
next to `select_candidate` / `set_finish` (headset actions, relayed as they are):

```python
{"type": "function", "function": {"name": "edit_part",
  "description": "Open the isolated Edit view on a placed part: turn it with arrows, colour it, move it. 'edit it', 'adjust the dishwasher'.",
  "parameters": {"type": "object", "properties": {"part": {"type": "string", "description": "the part's id, name or kind; omit for the selected one"}}}}},
{"type": "function", "function": {"name": "delete_part",
  "description": "Delete a placed part (undoable on the headset). 'delete it', 'remove the new dishwasher'.",
  "parameters": {"type": "object", "properties": {"part": {"type": "string"}}}}},
{"type": "function", "function": {"name": "view_similar",
  "description": "Open the Catalog on parts like a placed one (its kind; fits the gap when it's in one). 'show me similar ones'.",
  "parameters": {"type": "object", "properties": {"part": {"type": "string"}}}}},
```

`delete_part` must not be confused with `remove_component`, which takes a *scene* part (a cabinet) out. The
description above says "placed part".
