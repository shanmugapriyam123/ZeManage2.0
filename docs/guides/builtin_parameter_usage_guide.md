# BuiltInParameter Usage Guide

## Overview

CBOX Manage now supports **type-safe, language-independent** parameter matching using Revit's `BuiltInParameter` enum. This is the **preferred method** for built-in Revit parameters.

---

## Why Use BuiltInParameter?

### ✅ **Advantages**

1. **Type-Safe**: Compile-time validation (no typos)
2. **Language-Independent**: Works in all Revit language versions
3. **Performance**: Direct parameter access (no string lookup)
4. **Autocomplete**: IntelliSense support in Visual Studio

### ❌ **String-Based Parameters (Legacy)**

```json
"parameters": {
  "Structural": "true"  // ❌ Language-dependent, typo-prone
}
```

### ✅ **BuiltInParameter Enum (Preferred)**

```json
"builtInParameters": {
  "WALL_STRUCTURAL_SIGNIFICANT": {
    "value": "1",
    "operator": 0
  }
}
```

---

## JSON Schema

### Rule Structure

```json
{
  "ruleId": "RULE-001",
  "name": "My Rule",
  "builtInParameters": {
    "BUILTIN_PARAM_NAME": {
      "value": "expected_value",
      "operator": 0,
      "ignoreCase": true
    }
  }
}
```

### Operator Values

| Operator | Value | Description | Example |
|----------|-------|-------------|---------|
| `Equals` | 0 | Exact match | `"value": "90"` |
| `NotEquals` | 1 | Not equal | `"value": "0"` |
| `Contains` | 2 | String contains | `"value": "STRUCT"` |
| `StartsWith` | 3 | String starts with | `"value": "EXT"` |
| `GreaterThan` | 4 | Numeric > | `"value": "100"` |
| `LessThan` | 5 | Numeric < | `"value": "50"` |
| `IsEmpty` | 6 | Null or empty | `"value": ""` |
| `IsNotEmpty` | 7 | Has value | `"value": ""` |

---

## Common BuiltInParameters

### **Walls**

```json
{
  "builtInParameters": {
    "WALL_STRUCTURAL_SIGNIFICANT": {
      "value": "1",
      "operator": 0,
      "ignoreCase": true
    },
    "WALL_ATTR_WIDTH_PARAM": {
      "value": "0.5",
      "operator": 4,
      "ignoreCase": false
    }
  }
}
```

### **Doors**

```json
{
  "builtInParameters": {
    "DOOR_FIRE_RATING": {
      "value": "",
      "operator": 7,
      "ignoreCase": true
    },
    "DOOR_WIDTH": {
      "value": "3.0",
      "operator": 4,
      "ignoreCase": false
    }
  }
}
```

### **General Element Parameters**

```json
{
  "builtInParameters": {
    "ALL_MODEL_MARK": {
      "value": "DEMO",
      "operator": 2,
      "ignoreCase": true
    },
    "LEVEL_PARAM": {
      "value": "",
      "operator": 7,
      "ignoreCase": true
    },
    "PHASE_CREATED": {
      "value": "1",
      "operator": 0,
      "ignoreCase": false
    },
    "PHASE_DEMOLISHED": {
      "value": "",
      "operator": 7,
      "ignoreCase": true
    }
  }
}
```

### **Structural Elements**

```json
{
  "builtInParameters": {
    "STRUCTURAL_MATERIAL_PARAM": {
      "value": "Concrete",
      "operator": 2,
      "ignoreCase": true
    },
    "INSTANCE_STRUCT_USAGE_PARAM": {
      "value": "1",
      "operator": 0,
      "ignoreCase": false
    }
  }
}
```

### **Type Parameters**

```json
{
  "builtInParameters": {
    "ALL_MODEL_TYPE_NAME": {
      "value": "Basic Wall",
      "operator": 0,
      "ignoreCase": true
    },
    "ALL_MODEL_FAMILY_NAME": {
      "value": "System Family: Wall",
      "operator": 2,
      "ignoreCase": true
    }
  }
}
```

---

## Finding BuiltInParameter Names

### Method 1: Revit API Documentation

https://www.revitapidocs.com/2024/fb011c91-be7e-f737-28c7-3f1e1917a0e0.htm

### Method 2: RevitLookup (Recommended)

1. Install RevitLookup add-in
2. Select element in Revit
3. Use "Snoop Current Selection"
4. Find parameter → Note the `BuiltInParameter` enum name

### Method 3: Code

```csharp
// Get all parameters on an element
foreach (Parameter param in element.Parameters)
{
    if (param.Id.IntegerValue < 0)
    {
        BuiltInParameter bip = (BuiltInParameter)param.Id.IntegerValue;
        Console.WriteLine($"{param.Definition.Name} → {bip}");
    }
}
```

---

## Mixing String and BuiltInParameters

You can use **both** in the same rule:

```json
{
  "ruleId": "MIXED-PARAMS",
  "name": "Mixed Parameter Rule",
  "parameters": {
    "My Custom Parameter": {
      "value": "CustomValue",
      "operator": 0,
      "ignoreCase": true
    }
  },
  "builtInParameters": {
    "WALL_STRUCTURAL_SIGNIFICANT": {
      "value": "1",
      "operator": 0,
      "ignoreCase": true
    }
  }
}
```

**Evaluation Logic**: ALL conditions (both string and BuiltInParameter) must match (implicit AND).

---

## Examples

### Example 1: Prevent Structural Wall Deletion

```json
{
  "ruleId": "STRUCT-WALL-DELETE",
  "name": "Prevent Structural Wall Deletion",
  "mode": 3,
  "priority": 100,
  "categoryId": -2000011,
  "commandIds": [32778],
  "message": "Cannot delete structural walls",
  "builtInParameters": {
    "WALL_STRUCTURAL_SIGNIFICANT": {
      "value": "1",
      "operator": 0,
      "ignoreCase": true
    }
  }
}
```

### Example 2: Guide Fire-Rated Door Changes

```json
{
  "ruleId": "FIRE-DOOR-GUIDE",
  "name": "Fire-Rated Door Guidance",
  "mode": 2,
  "priority": 90,
  "categoryId": -2000023,
  "commandIds": [32778, 33066],
  "message": "Fire-rated door - verify with MEP team",
  "builtInParameters": {
    "DOOR_FIRE_RATING": {
      "value": "",
      "operator": 7,
      "ignoreCase": true
    }
  }
}
```

### Example 3: Monitor Phase Demolition

```json
{
  "ruleId": "PHASE-DEMO-MONITOR",
  "name": "Monitor Demolition",
  "mode": 1,
  "priority": 50,
  "commandIds": [33310],
  "builtInParameters": {
    "PHASE_DEMOLISHED": {
      "value": "",
      "operator": 7,
      "ignoreCase": true
    }
  }
}
```

---

## Reference: Common BuiltInParameter Values

| Category | BuiltInParameter | Description |
|----------|------------------|-------------|
| **All** | `ALL_MODEL_MARK` | Element Mark |
| **All** | `ALL_MODEL_TYPE_NAME` | Type Name |
| **All** | `ALL_MODEL_FAMILY_NAME` | Family Name |
| **All** | `LEVEL_PARAM` | Level |
| **All** | `PHASE_CREATED` | Phase Created |
| **All** | `PHASE_DEMOLISHED` | Phase Demolished |
| **Wall** | `WALL_STRUCTURAL_SIGNIFICANT` | Structural |
| **Wall** | `WALL_ATTR_WIDTH_PARAM` | Width |
| **Door** | `DOOR_FIRE_RATING` | Fire Rating |
| **Door** | `DOOR_WIDTH` | Width |
| **Door** | `DOOR_HEIGHT` | Height |
| **Structural** | `STRUCTURAL_MATERIAL_PARAM` | Structural Material |
| **Structural** | `INSTANCE_STRUCT_USAGE_PARAM` | Structural Usage |

---

## Performance Tips

1. **Prefer BuiltInParameter** over string lookups
2. Use `IsEmpty` (operator 6) or `IsNotEmpty` (operator 7) for existence checks
3. Combine with `categoryId` filtering for faster evaluation
4. Sort rules by `priority` (higher = evaluated first)

---

## Migration from String Parameters

**Before (String-based):**
```json
"parameters": {
  "Mark": {
    "value": "DEMO",
    "operator": 2
  }
}
```

**After (BuiltInParameter):**
```json
"builtInParameters": {
  "ALL_MODEL_MARK": {
    "value": "DEMO",
    "operator": 2,
    "ignoreCase": true
  }
}
```

---

## Troubleshooting

### Issue: Parameter not found

**Cause**: Parameter doesn't exist on element or wrong BuiltInParameter enum

**Solution**: 
1. Use RevitLookup to verify parameter exists
2. Check element category supports the parameter
3. Verify BuiltInParameter spelling

### Issue: Comparison not working

**Cause**: Wrong operator or value type mismatch

**Solution**:
1. Check parameter storage type (String/Integer/Double/ElementId)
2. Use correct operator for data type
3. For ElementId, compare integer values as strings

### Issue: Rule not matching

**Cause**: Multiple parameter conditions (implicit AND)

**Solution**:
1. Verify ALL parameter conditions match
2. Use separate rules for OR logic
3. Check logs for detailed evaluation trace

---

## See Also

- [Rule Schema Documentation](./rule_schema_guide.md)
- [Command Coverage List](./Postable%20Command%20IDs.xlsx)
- [Revit API BuiltInParameter Enum](https://www.revitapidocs.com/2024/fb011c91-be7e-f737-28c7-3f1e1917a0e0.htm)
