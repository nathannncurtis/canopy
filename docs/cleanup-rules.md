# Cleanup rule format

Cleanup rules are declarative JSON used only to preview candidates. The engine has no delete or command-execution API.

```json
{
  "version": 1,
  "rules": [{
    "id": "large-logs",
    "reason": "Large log files",
    "risk": "Low",
    "kind": "Files",
    "pathPattern": "*\\cache\\*",
    "namePattern": "*.log",
    "extensions": ["log"],
    "minimumSize": 10485760,
    "minimumDepth": 2
  }]
}
```

Patterns support `*` and `?` wildcards. Predicates within a rule are ANDed; matching rules are combined per item. Risk is the highest matching rule risk. Reclaim totals count each matched node once. Rules without a predicate, duplicate IDs, invalid ranges, unsupported versions, oversized inputs, and malformed topology are rejected.

Applications must present the preview and obtain separate explicit user confirmation before implementing any filesystem action.
