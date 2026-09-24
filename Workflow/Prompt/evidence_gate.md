Führe ein Evidence Gate durch.

Akzeptiere eine Lösung nur, wenn:
- jede geänderte Komponente durch Repository-Evidenz begründet ist
- jede zentrale Behauptung ein Beleg oder ein reproduzierbarer Test besitzt
- alle blocker- und major-Befunde geschlossen sind
- die Acceptance Criteria mit konkreten Prüfungen verbunden sind
- keine offenen Annahmen als Fakten dargestellt werden
- der Scope gegenüber dem ursprünglichen Ziel begründet ist

Erstelle:
1. Traceability Matrix
2. offene Risiken
3. nicht verifizierte Annahmen
4. ausgeführte Prüfungen mit Ergebnissen
5. finale Entscheidung: pass / conditional pass / fail


Die Traceability Matrix könnte so aussehen:

| Requirement | Code/Artifact | Test/Evidence | Status |
|---|---|---|---|
| Structured logging | src/Logging/... | test command output | Verified |
| Request correlation | middleware/... | integration test | Verified |
| Legacy client support | API adapter/... | not tested | Assumed |


schreibe deine Ergebnisse in eine `evidence_gate.md` in den {tasktitel} Ordner.
