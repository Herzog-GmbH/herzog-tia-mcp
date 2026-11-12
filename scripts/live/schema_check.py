"""Prueft structuredContent gegen das outputSchema eines Werkzeugs, wie es ein MCP-Client tut.

Claude Code lehnt eine Antwort ab, die nicht zum Schema passt, und zeigt dem Modell nur den Fehler, obwohl das
Werkzeug gelaufen ist. Ein unbekanntes Schluesselwort gilt selbst als Verstoss: Was niemand prueft, darf nicht
unbemerkt durchgehen. Gegenstueck zu TiaMcpServer.Tests/TestSupport/JsonSchemaCheck.cs.
"""
import re

ANNOTATIONS = {"$schema", "$id", "$comment", "title", "description", "default", "examples", "format",
               "deprecated", "readOnly", "writeOnly", "$defs", "definitions"}


def violations(schema, document):
    found = []
    _check(schema, schema, document, "$", found)
    return found


def _check(root, schema, value, path, found):
    if schema is True:
        return
    if schema is False:
        found.append(f"{path}: das Schema erlaubt hier keinen Wert")
        return
    for keyword, expected in schema.items():
        if keyword == "$ref":
            _check(root, _resolve(root, expected), value, path, found)
        elif keyword == "type":
            if not _matches_type(expected, value):
                found.append(f"{path}: muss {expected} sein, ist {type(value).__name__}")
        elif keyword == "enum":
            if not any(_equal(entry, value) for entry in expected):
                found.append(f"{path}: {value!r} ist keiner von {expected}")
        elif keyword == "const":
            if not _equal(expected, value):
                found.append(f"{path}: muss {expected!r} sein, ist {value!r}")
        elif keyword == "required":
            if isinstance(value, dict):
                for name in expected:
                    if name not in value:
                        found.append(f"{path}: must have required property '{name}'")
        elif keyword == "properties":
            if isinstance(value, dict):
                for name, member in value.items():
                    if name in expected:
                        _check(root, expected[name], member, f"{path}.{name}", found)
        elif keyword == "additionalProperties":
            if isinstance(value, dict):
                declared = schema.get("properties", {})
                for name, member in value.items():
                    if name not in declared:
                        _check(root, expected, member, f"{path}.{name}", found)
        elif keyword == "items":
            if isinstance(value, list):
                for index, item in enumerate(value):
                    _check(root, expected, item, f"{path}[{index}]", found)
        elif keyword == "anyOf":
            if _count_matching(root, expected, value, path) == 0:
                found.append(f"{path}: passt zu keinem Eintrag von anyOf")
        elif keyword == "oneOf":
            matching = _count_matching(root, expected, value, path)
            if matching != 1:
                found.append(f"{path}: passt zu {matching} Eintraegen von oneOf, erlaubt ist genau 1")
        elif keyword == "allOf":
            for entry in expected:
                _check(root, entry, value, path, found)
        elif keyword == "minimum":
            if _is_number(value) and value < expected:
                found.append(f"{path}: muss mindestens {expected} sein")
        elif keyword == "maximum":
            if _is_number(value) and value > expected:
                found.append(f"{path}: darf hoechstens {expected} sein")
        elif keyword == "minLength":
            if isinstance(value, str) and len(value) < expected:
                found.append(f"{path}: braucht mindestens {expected} Zeichen")
        elif keyword == "maxLength":
            if isinstance(value, str) and len(value) > expected:
                found.append(f"{path}: darf hoechstens {expected} Zeichen haben")
        elif keyword == "minItems":
            if isinstance(value, list) and len(value) < expected:
                found.append(f"{path}: braucht mindestens {expected} Eintraege")
        elif keyword == "maxItems":
            if isinstance(value, list) and len(value) > expected:
                found.append(f"{path}: darf hoechstens {expected} Eintraege haben")
        elif keyword == "pattern":
            if isinstance(value, str) and not re.search(expected, value):
                found.append(f"{path}: passt nicht zum Muster {expected}")
        elif keyword not in ANNOTATIONS:
            found.append(f"{path}: das Schluesselwort '{keyword}' prueft dieser Client nicht")


def _count_matching(root, entries, value, path):
    count = 0
    for entry in entries:
        inner = []
        _check(root, entry, value, path, inner)
        if not inner:
            count += 1
    return count


def _is_number(value):
    return isinstance(value, (int, float)) and not isinstance(value, bool)


def _equal(left, right):
    return type(left) is type(right) and left == right or (_is_number(left) and _is_number(right) and left == right)


def _matches_type(expected, value):
    if isinstance(expected, list):
        return any(_matches_type(entry, value) for entry in expected)
    if expected == "object":
        return isinstance(value, dict)
    if expected == "array":
        return isinstance(value, list)
    if expected == "string":
        return isinstance(value, str)
    if expected == "boolean":
        return isinstance(value, bool)
    if expected == "null":
        return value is None
    if expected == "number":
        return _is_number(value)
    if expected == "integer":
        return _is_number(value) and float(value).is_integer()
    raise ValueError(f"Unbekannter Schematyp {expected}")


def _resolve(root, reference):
    if reference != "#" and not reference.startswith("#/"):
        raise ValueError(f"Nur lokale Verweise werden geprueft, nicht {reference}")
    current = root
    for raw in reference.split("/")[1:]:
        part = raw.replace("~1", "/").replace("~0", "~")
        current = current[int(part)] if isinstance(current, list) else current[part]
    return current
