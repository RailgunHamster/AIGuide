#if TOOLS
#nullable enable
namespace AIGuide;

/// <summary>MCP tools/list 返回的工具定义（JSON Schema）。$TARGET 会被替换为目标参数的通用 schema。</summary>
internal static class ToolDefs
{
    public const string TargetSchema = """
{"description": "Element: ref from ui_snapshot/ui_find (\"e12\", \"e12.40\"), a locator starting with \"/\" (from ui_describe; supports ** and ::tree=/::tab=/::menu=/::list=/::bar= suffixes), or a query object like {\"text\":\"Save\",\"class\":\"Button\"}. A bare string that is not a ref/locator is treated as {\"text\": ...}.", "anyOf": [{"type": "string"}, {"type": "object"}]}
""";

    public const string Json = """
[
 {
  "name": "editor_state",
  "annotations": {
   "readOnlyHint": true
  },
  "description": "Current editor context: Godot version, project, edited scene, open scenes, selected nodes, inspected object, main screen (2D/3D/Script…), visible windows/popups, focused control, editor language, active guide.",
  "inputSchema": {
   "type": "object",
   "properties": {}
  }
 },
 {
  "name": "scene_tree",
  "annotations": {
   "readOnlyHint": true
  },
  "description": "Node tree of the edited scene (semantic data, not UI): path, class, script, and optional property values.",
  "inputSchema": {
   "type": "object",
   "properties": {
    "root": {
     "type": "string",
     "description": "Node path relative to the scene root. Default: scene root."
    },
    "depth": {
     "type": "integer",
     "default": 6
    },
    "properties": {
     "type": "array",
     "items": {
      "type": "string"
     },
     "description": "Property names to print for each node that has them, e.g. [\"material_override\",\"mesh\"]."
    }
   }
  }
 },
 {
  "name": "ui_snapshot",
  "annotations": {
   "readOnlyHint": true
  },
  "description": "Text outline of the live editor UI: one line per meaningful control with ref, class, #name, \"text\", tooltip, property/value for inspector rows, {flags}, and @x,y WxH (window canvas coords). Also lists tree items, tabs, list items, popup menu items and menu-bar entries as sub-refs. Layout-only containers are folded away (compact). Start with the whole editor, then narrow with root.",
  "inputSchema": {
   "type": "object",
   "properties": {
    "root": $TARGET,
    "max_nodes": {
     "type": "integer",
     "default": 600
    },
    "max_items": {
     "type": "integer",
     "default": 40,
     "description": "Max sub-items listed per tree/list/menu."
    },
    "visible_only": {
     "type": "boolean",
     "default": true
    },
    "compact": {
     "type": "boolean",
     "default": true
    }
   }
  }
 },
 {
  "name": "ui_find",
  "annotations": {
   "readOnlyHint": true
  },
  "description": "Search controls and sub-items (tree items, tabs, list/menu items, menu-bar entries). Text matches are case-insensitive substrings unless exact. Returns refs, rects and locators, best match first.",
  "inputSchema": {
   "type": "object",
   "properties": {
    "text": {
     "type": "string"
    },
    "exact": {
     "type": "boolean",
     "default": false
    },
    "class": {
     "type": "string",
     "description": "Godot class, matches subclasses (e.g. Button, EditorProperty, Tree)."
    },
    "name": {
     "type": "string",
     "description": "Exact node name."
    },
    "tooltip": {
     "type": "string"
    },
    "property": {
     "type": "string",
     "description": "Inspector property path, e.g. surface_material_override/0."
    },
    "kind": {
     "type": "string",
     "enum": [
      "any",
      "control",
      "tree",
      "tab",
      "list",
      "menu",
      "bar"
     ],
     "default": "any"
    },
    "under": $TARGET,
    "visible_only": {
     "type": "boolean",
     "default": true
    },
    "limit": {
     "type": "integer",
     "default": 20
    }
   }
  }
 },
 {
  "name": "ui_describe",
  "annotations": {
   "readOnlyHint": true
  },
  "description": "Full info about one element: persistent locator, rect, visible rect, window, and hidden_because (inactive tab / folded inspector section / collapsed tree item / closed window / scrolled out) with refs of what to click to reveal it.",
  "inputSchema": {
   "type": "object",
   "properties": {
    "target": $TARGET
   },
   "required": [
    "target"
   ]
  }
 },
 {
  "name": "ui_act",
  "description": "Perform a real input action in the editor (synthesized OS-level mouse/keyboard events; modal dialogs block it exactly like for a human). Waits a few frames and reports new/closed windows and the focused control.",
  "inputSchema": {
   "type": "object",
   "properties": {
    "action": {
     "type": "string",
     "enum": [
      "click",
      "double_click",
      "right_click",
      "middle_click",
      "hover",
      "scroll",
      "drag",
      "type",
      "paste",
      "key",
      "focus"
     ]
    },
    "target": $TARGET,
    "x": {
     "type": "number",
     "description": "Canvas x in the window (instead of target)."
    },
    "y": {
     "type": "number"
    },
    "window": {
     "description": "Window ref/locator for x,y or key (default: main or target's window).",
     "anyOf": [
      {
       "type": "string"
      },
      {
       "type": "object"
      }
     ]
    },
    "to": {
     "description": "Drag destination: element or {\"x\":..,\"y\":..}.",
     "anyOf": [
      {
       "type": "string"
      },
      {
       "type": "object"
      }
     ]
    },
    "text": {
     "type": "string",
     "description": "For type: text to type (clicks the target first if given; \\n presses Enter). For paste: text put on the clipboard, then Ctrl+A, Ctrl+V into the target - use this for code, since code editors auto-indent/auto-close brackets while typing."
    },
    "keys": {
     "type": "string",
     "description": "For key: combo like \"ctrl+s\", \"Enter\", \"Escape\", \"F5\"."
    },
    "amount": {
     "type": "integer",
     "description": "For scroll: wheel steps, positive = down.",
     "default": 3
    },
    "modifiers": {
     "type": "object",
     "properties": {
      "ctrl": {
       "type": "boolean"
      },
      "shift": {
       "type": "boolean"
      },
      "alt": {
       "type": "boolean"
      },
      "meta": {
       "type": "boolean"
      }
     }
    },
    "show": {
     "type": "boolean",
     "default": false,
     "description": "Highlight the target for ~0.7s before acting (demonstration mode)."
    },
    "focus_window": {
     "type": "boolean",
     "default": true,
     "description": "Bring the editor window to the OS foreground first (popups may close if the window is not focused)."
    },
    "move_cursor": {
     "type": "boolean",
     "default": true,
     "description": "Fallback only: if synthesized events cannot put the target into hover state (unpatched engine and the OS cursor is outside this window), briefly move the real OS cursor onto it and restore it afterwards. Buttons require hover. With the engine patch (godot/patches/ai_guide_synthetic_hover.patch) this is never needed."
    }
   },
   "required": [
    "action"
   ]
  }
 },
 {
  "name": "ui_highlight",
  "description": "Show highlight boxes (numbered when several) on elements, with an optional callout text. Non-blocking: the user can keep working. Use for 'where is X?' questions.",
  "inputSchema": {
   "type": "object",
   "properties": {
    "targets": {
     "type": "array",
     "items": $TARGET
    },
    "text": {
     "type": "string"
    },
    "duration_ms": {
     "type": "integer",
     "default": 6000,
     "description": "0 = until cleared."
    },
    "clear": {
     "type": "boolean",
     "default": false,
     "description": "Remove existing highlights first (or only, if no targets)."
    }
   }
  }
 },
 {
  "name": "ui_guide",
  "description": "Start an interactive step-by-step guide for the user. Each step highlights its target with a numbered box and a callout; the guide advances when the user clicks the target (until=\"click\", default), when a condition holds, or via guide_control. Targets that do not exist yet are shown as 'waiting' and picked up automatically once they appear. Replaces any running guide.",
  "inputSchema": {
   "type": "object",
   "properties": {
    "title": {
     "type": "string"
    },
    "steps": {
     "type": "array",
     "items": {
      "type": "object",
      "properties": {
       "target": $TARGET,
       "text": {
        "type": "string",
        "description": "Short instruction shown in the callout."
       },
       "until": {
        "description": "\"click\" (default) | \"manual\" | {\"visible\": target} (revealed: visible or merely scrolled out; not folded/hidden) | {\"exists\": target} (node exists at all) | {\"selected\": \"node/path\"} | {\"inspecting\": \"Class|res://path|node/path\"} | {\"main_screen\": \"3D\"}",
        "anyOf": [
         {
          "type": "string"
         },
         {
          "type": "object"
         }
        ]
       }
      },
      "required": [
       "target",
       "text"
      ]
     }
    },
    "dim": {
     "type": "boolean",
     "default": true,
     "description": "Dim everything except the current target."
    },
    "auto_scroll": {
     "type": "boolean",
     "default": true
    }
   },
   "required": [
    "steps"
   ]
  }
 },
 {
  "name": "guide_status",
  "annotations": {
   "readOnlyHint": true
  },
  "description": "Progress of the current guide: state, current step, whether its target is found/visible (and why not), completed steps, and a log of recent user/agent clicks (on or off target, with what was clicked).",
  "inputSchema": {
   "type": "object",
   "properties": {}
  }
 },
 {
  "name": "guide_control",
  "description": "Control the running guide: next/prev/restart/stop, or do_step = perform the current step's click for the user (visible demonstration), which then advances. The user has the same prev / restart / exit buttons on the guide bubble.",
  "inputSchema": {
   "type": "object",
   "properties": {
    "action": {
     "type": "string",
     "enum": [
      "next",
      "prev",
      "restart",
      "stop",
      "do_step"
     ]
    },
    "rollback": {
     "type": "boolean",
     "default": true,
     "description": "restart only: first roll the editor back to how it was when the guide started (same rollback as rehearsals)."
    }
   },
   "required": [
    "action"
   ]
  }
 },
 {
  "name": "editor_screenshot",
  "annotations": {
   "readOnlyHint": true
  },
  "description": "PNG screenshot of an editor window (default: main), optionally cropped around a target. Includes the overlay.",
  "inputSchema": {
   "type": "object",
   "properties": {
    "window": {
     "anyOf": [
      {
       "type": "string"
      },
      {
       "type": "object"
      }
     ]
    },
    "target": $TARGET,
    "margin": {
     "type": "integer",
     "default": 80
    },
    "max_width": {
     "type": "integer",
     "default": 1600
    }
   }
  }
 },
 {
  "name": "editor_exec",
  "description": "Run a GDScript snippet inside the editor as the body of `func run():` (tool context; EditorInterface etc. available; `await` allowed; `return` a value to get it back). The plugin itself is C#; this is only an escape hatch for semantic queries/changes that have no UI equivalent you need to teach. Output: returned value + any printed errors/messages.",
  "inputSchema": {
   "type": "object",
   "properties": {
    "code": {
     "type": "string"
    }
   },
   "required": [
    "code"
   ]
  }
 },
 {
  "name": "rehearsal_start",
  "description": "Start rehearsing a procedure before teaching it. Snapshots editor state (undo histories, files, open scenes, selection, windows). Every ui_act until rehearsal_stop is recorded with a stable locator, its effect, and the selection change.",
  "inputSchema": {
   "type": "object",
   "properties": {
    "title": {
     "type": "string"
    }
   }
  }
 },
 {
  "name": "rehearsal_stop",
  "description": "Stop rehearsing. Rolls the editor back (undoes the rehearsal's actions, deletes files it created, closes scenes/windows/shader tabs it opened, restores selection and main screen; reports what could not be rolled back) and converts the recording into guide steps (target locator + fallback query + default instruction + completion condition). Optionally saves them as a reusable recipe and/or starts the guide for the user right away.",
  "inputSchema": {
   "type": "object",
   "properties": {
    "rollback": {
     "type": "boolean",
     "default": true
    },
    "title": {
     "type": "string",
     "description": "Guide/recipe title (default: rehearsal title)."
    },
    "texts": {
     "type": "array",
     "items": {
      "type": "string"
     },
     "description": "Instruction text per generated step, in order (empty string keeps the default). Write them in the user's language."
    },
    "save_as": {
     "type": "string",
     "description": "Recipe name to save under res://.ai_guide/recipes/."
    },
    "start_guide": {
     "type": "boolean",
     "default": false
    },
    "dim": {
     "type": "boolean",
     "default": true
    }
   }
  }
 },
 {
  "name": "recipe_list",
  "annotations": {
   "readOnlyHint": true
  },
  "description": "List saved recipes (rehearsed procedures) in this project.",
  "inputSchema": {
   "type": "object",
   "properties": {}
  }
 },
 {
  "name": "recipe_play",
  "description": "Start the guide for a saved recipe.",
  "inputSchema": {
   "type": "object",
   "properties": {
    "name": {
     "type": "string"
    },
    "title": {
     "type": "string"
    },
    "texts": {
     "type": "array",
     "items": {
      "type": "string"
     }
    },
    "dim": {
     "type": "boolean",
     "default": true
    }
   },
   "required": [
    "name"
   ]
  }
 },
 {
  "name": "ai_guide_reload",
  "description": "Developer tool: rebuild the C# project (dotnet build) and reload the plugin. Use after editing the plugin's .cs files. Requests in flight during the reload are dropped; wait ~3s, then call again.",
  "inputSchema": {
   "type": "object",
   "properties": {}
  }
 }
]
""";
}
#endif
