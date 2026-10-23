Lies diese beiden Dokumente vollständig:

- {spec_path}
- {plan_path}

Zerlege den vorhandenen Implementierungsplan in kleine, einzeln ausführbare Aufgaben für
voneinander unabhängige Claude-Code-Sessions.

## Verzeichnisse

Die Anwendung setzt diese Werte ein. Alle drei Pfade sind **absolut** und vollständig — setze
keinen davor und leite keinen daraus ab:

- `{workflow_path}` — der ausgecheckte Workflows-Ordner. Du schreibst nicht direkt hierhin.
- `{task_path}` — der Ordner dieses Tasks (`{tasktitel}`). Hierhin gehören die
  aufgabenübergreifenden Dateien.
- `{subtask_path}` — der Ordner, der ausschließlich die Subtask-Ordner enthält.

Die einzige Zusammensetzung, die du selbst vornimmst, ist das Anhängen eines Subtask-Namens:

- Subtask-Ordner = `{subtask_path}/<subtask_title>`

`<subtask_title>` ist ein Name, den du je Subtask selbst vergibst. Er steht absichtlich in spitzen
Klammern: geschweifte Klammern sind für Werte reserviert, die die Anwendung ersetzt. Ein Name in
spitzen Klammern wird **nicht** ersetzt — du setzt dort deinen eigenen Namen ein.

`<subtask_title>` muss ein einfacher Ordnername sein. Unsicher und von der Anwendung abgelehnt ist
ein Name, der

- einen Pfadtrenner enthält — `\`, `/` oder `:`,
- genau `.` oder `..` lautet,
- ein für Dateinamen unzulässiges Zeichen enthält (zum Beispiel `*`, `?`, `"`, `<`, `>`, `|`).

Ein abgelehnter Name kostet die betroffene Aufgabe: die Anwendung zählt sie als fehlgeschlagen und
führt sie nicht aus. Zwei Punkte innerhalb eines längeren Namens sind dagegen erlaubt.
Empfohlen ist `ST-001-kurzer-titel`, fortlaufend nummeriert in Ausführungsreihenfolge.

## Was du erzeugst

Für jede Aufgabe einen Ordner `{subtask_path}/<subtask_title>` mit:

- `subtask.md` — die vollständige Aufgabenbeschreibung. Darf nicht leer sein; ohne sie zählt die
  Anwendung die Aufgabe als fehlgeschlagen.
- `findings.md` — Probleme, die dir bei dieser Aufgabe auffallen.
- `status.json` — der Statusbericht. Lege ihn sofort mit dem Anfangswert `pending` an:

```json
{
  "subtask": "<subtask_title>",
  "status": "pending",
  "failreason": null,
  "openFindings": 0,
  "workflowRelevantFindings": [],
  "testsPassed": false,
  "readyForVerification": false
}
```

Die WPF-Anwendung liest aus dieser Datei ausschließlich `status` und `failreason`. Als Erfolg gilt
`status` nur mit dem Wert `complete`; `failed` gilt als Fehlschlag, und jeder andere Wert —
einschließlich `pending` — bedeutet „noch nicht fertig“. Die Anwendung zeigt daraus zum Beispiel
„12 von 25 subtasks abgeschlossen“ und „3 subtasks failed!“ an.

Aufgabenübergreifende Erkenntnisse gehören in die globale Datei `{task_path}/findings.md`, neben
den Subtask-eigenen `findings.md`. In `{subtask_path}` liegen ausschließlich Subtask-Ordner.

## Anforderungen an eine Subtask

Eine Subtask muss:

- genau ein zusammenhängendes, überprüfbares Ergebnis besitzen,
- in einer einzelnen frischen Claude-Code-Session ausführbar sein,
- alle benötigten Planstellen, Dateien, Entscheidungen und Randbedingungen selbst enthalten,
- ohne Kenntnis vorheriger Chatverläufe verständlich sein,
- Voraussetzungen und Abhängigkeiten explizit nennen,
- konkrete Implementierungsschritte enthalten,
- konkrete Tests und Abschlusskriterien enthalten,
- ausdrücklich nennen, was nicht verändert werden darf.

Eine Subtask darf nicht:

- mehrere unabhängige Komponenten oder Verantwortungsgrenzen vermischen,
- Implementierung, systemweite Integration, Betriebsumstellung und Gesamtabnahme in einer Aufgabe
  bündeln,
- stillschweigend Informationen aus den Quelldokumenten voraussetzen,
- neue Architekturentscheidungen erfinden.

Wenn eine Aufgabe voraussichtlich mehr als eine Claude-Code-Session benötigt, teile sie weiter auf.
Bevorzuge kleine Aufgaben gegenüber großen Aufgaben.

## Unbeaufsichtigter Lauf

Die Subtasks laufen ohne Menschen. Daraus folgt:

- Es gibt **keine Menschenaufgaben**. Alles, was eine Desktop-Anwendung oder einen UI-Test startet, ist ein
  Desktop-Job: Die Subtask beschreibt die Schritte als PowerShell-Skripte, die Session fordert sie über
  `desktopjob.json` an und prüft in einer Folgesession die Lieferung. Teile lange Abläufe in Schritte mit eigenem
  Marker und eigener Zeitgrenze (höchstens 240 Minuten je Schritt).
- Jedes Gate, jede Freigabe und jede Frage an den User bekommt einen Eintrag in `{task_path}/decisions.md`. Die
  Subtask verweist auf die Eintrags-ID (`D-NNN`) und beschreibt, wie die Session die Antwort anwendet. Die Anwendung
  erzeugt `decisions.md` nach der Zerlegung und lässt sie vom User einmal freigeben.
- Eine Subtask setzt nie `waiting_for_human` oder `waiting_for_user`. Zulässig sind `complete`, `failed`,
  `deferred` (Desktop-Job angefordert), `blocked`, `pending` und `in_progress`.
- Die Anwendung startet eine Subtask erst, wenn alle ihre Abhängigkeiten `complete` sind. Eine eigene
  Vorgängerprüfung in `subtask.md` ist nicht nötig; wer sie trotzdem einbaut, liest den Index im Format von
  Version 2 (Einträge sind Text oder Objekt).

Verwende dieses Format für jede `subtask.md`:

# ST-XXX: Titel

## Ziel
## Warum diese Aufgabe separat ist
## Voraussetzungen
## Abhängigkeiten
## Verbindliche Quellen
## Betroffene Dateien
## Nicht betroffen
## Implementierungsschritte
## Tests und Verifikation
## Abschlusskriterien
## Übergabe an Folgeaufgaben
## Prompt für die ausführende Claude-Code-Session

## Review

Führe nach der Erzeugung einen unabhängigen Reviewdurchgang mit einem frischen Subagenten durch.
Prüfe:

1. vollständige Abdeckung beider Quelldokumente,
2. keine verlorenen Constraints oder Abnahmekriterien,
3. keine übergroßen Aufgaben,
4. keine zyklischen oder versteckten Abhängigkeiten,
5. keine überlappende Datei- oder Komponentenverantwortung,
6. jede Aufgabe ist aus ihrem Markdown allein ausführbar.

Korrigiere gefundene Probleme und aktualisiere anschließend die betroffenen `findings.md`.

Verändere die beiden Quelldokumente nicht. Implementiere keinen Produktivcode.

## Abschluss — die Flag-Datei

Wenn alle Subtask-Ordner und -Dateien erzeugt sind, schreibst du als **letzte** Aktion die
Flag-Datei `{task_path}/result.json`. Sie liegt im Task-Ordner, nicht im Workflows-Ordner: der
Workflows-Ordner ist von allen Tasks gemeinsam benutzt, und eine dort liegengebliebene Flag-Datei
würde den nächsten Task sofort als fertig melden.

Diese Datei ist gleichzeitig der verbindliche Index in **Ausführungsreihenfolge** und **darf nicht leer sein**
— eine Datei mit 0 Byte erkennt die Anwendung als nicht vorhanden:

```json
{
  "version": 2,
  "task": "{tasktitel}",
  "subtasks": [
    "ST-001-erster-titel",
    { "title": "ST-002-zweiter-titel", "dependsOn": [] },
    { "title": "ST-003-dritter-titel", "dependsOn": ["ST-001-erster-titel"] }
  ]
}
```

Ein Eintrag ist entweder der Ordnername als Text oder ein Objekt mit `title` und `dependsOn`.

- Ohne `dependsOn` hängt die Aufgabe von **allen** vorher stehenden ab. Das ist der sichere Standard.
- `dependsOn: []` heißt unabhängig.
- `dependsOn` darf nur vorher stehende Titel nennen.

Setze `dependsOn` nur, wenn die Aufgabe nachweislich nicht von Reihenfolge, gemeinsamem Arbeitsstand oder früheren
Messungen abhängt. Im Zweifel lässt du das Feld weg.

Das Feld `"subtasks"` ist Pflicht und muss jeden erzeugten Ordnernamen genau einmal enthalten, in
genau der Reihenfolge, in der die Aufgaben abgearbeitet werden sollen. Die Anwendung leitet keine
Reihenfolge aus dem Verzeichnis ab. Fehlt das Feld, ist es leer oder kein Array, bricht die
Anwendung die Phase mit einer Fehlermeldung ab.

Schreibe `status.json` und `result.json` niemals direkt, sondern atomar über eine temporäre Datei:

1. nach `status.json.tmp` beziehungsweise `result.json.tmp` schreiben,
2. Schreiben abschließen, Datei schließen,
3. in `status.json` beziehungsweise `result.json` umbenennen.

Sonst liest die Anwendung eine halb geschriebene Datei. Schreibe zuerst die `status.json` jeder
Aufgabe, danach als letztes die nicht leere `result.json`: die Anwendung reagiert ausschließlich
auf die fertige `result.json`.
