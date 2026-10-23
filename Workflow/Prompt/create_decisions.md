Lies alle Subtask-Beschreibungen dieses Tasks vollständig: jede Datei `subtask.md` in den Ordnern unter
`{subtask_path}`, in der Reihenfolge von `{task_path}/result.json`. Lies außerdem `{spec_path}` und `{plan_path}`, soweit
eine Subtask darauf verweist.

Die Subtasks laufen danach **ohne Menschen**: niemand beantwortet Fragen, niemand legt Marker an, niemand trifft
Gate-Entscheidungen. Deine Aufgabe ist es, jede Stelle zu finden, an der eine Subtask einen Menschen braucht, und
dafür eine Vorab-Entscheidung vorzuschlagen. Der User prüft deine Datei einmal, bevor der Lauf beginnt; das ist der
letzte Kontakt mit einem Menschen.

## Was du suchst

- Gates und Freigaben (zum Beispiel „Der User entscheidet“, „Gate“, „Freigabe“, „Abnahme“, `entscheidung.md`).
- Rückfragen („User fragen“, „STOPP“, `AskUserQuestion`, „zur Bestätigung“).
- Menschenaufgaben (Marker, die ein Mensch anlegt, Schritte „für den Menschen“, Bedienung in einer IDE).
- Schritte, die eine Desktop-Anwendung oder einen UI-Test starten. Sie werden Desktop-Jobs der Anwendung.
- Offene Punkte in `{task_path}/findings.md`, die ausdrücklich auf eine Entscheidung des Users warten.

## Was du schreibst

Schreibe `{task_path}/decisions.md` atomar (in `decisions.md.tmp` schreiben, schließen, umbenennen). Die Datei
beginnt mit YAML-Frontmatter (`id`, `description`, `summary`, `paths`) und enthält je Frage einen Block:

```markdown
## D-001

- subtask: <ein oder mehrere Subtask-Titel>
- question: <die Frage wörtlich oder knapp zusammengefasst>
- default: <die vorgeschlagene Antwort; bei Rechenregeln die Regel mit Quelle>
- policy: use-default | desktop-job | fail
- answer:
```

- `policy: use-default` — die Session wendet `default` an (oder `answer`, falls der User sie füllt).
- `policy: desktop-job` — die Session fordert einen Desktop-Job an, statt auf einen Menschen zu warten.
- `policy: fail` — die Session endet mit `failed`; nur, wenn eine Entscheidung wirklich nicht vorab möglich ist.

Leite `default` aus Spezifikation, Plan und Findings ab, wo es eine Regel, einen Standardwert oder eine
Empfehlung gibt, und nenne die Quelle. Erfinde keine Architekturentscheidungen. Ein letzter Eintrag legt fest, was
gilt, wenn eine Session eine nicht gelistete Frage hat (`policy: fail`).

Ändere keine andere Datei. Schreibe keine `decisions.approved.json`; die schreibt die Anwendung, wenn der User den
Lauf freigibt.
