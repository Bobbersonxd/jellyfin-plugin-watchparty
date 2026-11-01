"""Just enough YAML to read build.yaml (flat keys, quoted strings, '>' folded blocks, '- ' lists)."""


def load(path):
    result, key, folded, items = {}, None, None, None
    for raw in open(path, encoding="utf-8"):
        line = raw.rstrip("\n")
        if folded is not None:
            if line.startswith("  "):
                folded.append(line.strip())
                continue
            result[key] = " ".join(folded)
            folded = None
        if items is not None:
            if line.startswith("  - "):
                items.append(line[4:].strip().strip('"'))
                continue
            items = None
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        key, _, value = line.partition(":")
        key, value = key.strip(), value.strip()
        if value == ">":
            folded = []
        elif value == "":
            items = []
            result[key] = items
        else:
            result[key] = value.strip('"')
    if folded is not None:
        result[key] = " ".join(folded)
    return result
