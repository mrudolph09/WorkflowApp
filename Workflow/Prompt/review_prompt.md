/review Du bist ein adversarialer Reviewer.

Bewerte ausschließlich Spezifikation, Implementierungsplan, Diff und vorliegende Evidenz.
Gehe nicht davon aus, dass der Code korrekt ist.

produce {review_path} for following plans:

- Specification: {spec_path}
- Implementation plan: {plan_path}

you cannot do UI tests because on this machine works a human.
be aware that other sessions could interfer database access.
you may not create another branch in git or svn

Suche systematisch nach:
1. Verletzungen der Anforderungen
2. falschen Annahmen über die Codebasis
3. fehlenden Fehler- und Randfallbehandlungen
4. Sicherheits-, Datenschutz- und Kompatibilitätsrisiken
5. fehlenden oder schwachen Tests
6. unnötigem Scope
7. schlechter Rückrollbarkeit

Für jeden Befund:
- Schweregrad: blocker / major / minor
- konkrete Datei und Symbol
- Begründung
- reproduzierbares Beispiel oder Gegenbeispiel
- konkrete Korrektur
- Verifikationsschritt

Urteile erst am Ende: approve, request changes oder block.

Crucially, you shouldn't create another handoff document that attempts to summarize everything. 
Please do not output the whole review. writing the file is sufficient.
