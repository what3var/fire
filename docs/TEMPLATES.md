# Templates

A **template** is a folder with the files that a new file or a new project is made of. The editor (spark) shows all templates in ONE flat list - the ones that ship with fire, your own and
those that packages bring, side by side - with an icon, a title, a short description and where it comes from (`Local` or `From PackageName 1.2.3`), and a search bar above.

```
File > New > Solution...      a solution with a project after a template (the open solution is closed first)
File > New > Project...       a project after a template, added to the open solution
File > New > Script...        a file after a code template (Script, Fire Class, FXML Window, FXML View, and yours)
File > New > Markdown         a new Markdown document
File > New > Raster Image     a new picture

right click on a solution     New Project...
right click on a project or   New > Script...            the list of code templates
a folder of it                    Fire Class / FXML Window / FXML View    only the name is asked
                                  Markdown Document... / Raster Image... / Other File...
```

The name is typed in a box, and the extension of the template is shown behind it and added automatically (`Greeter` + `.script`; a typed `.script` is not doubled). A project or a solution also
asks for the folder.

## Where templates are

| folder | what |
|---|---|
| `Templates/` next to the program | what ships with fire (`Code/` and `Project/`) |
| `~/spark/templates/` | your own, the same layout |
| `Templates/Package/Name_1.2.3.4/` (and the same below `~/spark/templates/`) | templates of a package, laid out by hand: `Code/` and `Project/` (or `Projekt/`) below it |
| `templates/` in an installed package | what a package brings itself (below) |

Below each of these, `Code/Title/` is a **code template** (files for a project that exists) and `Project/Title/` a **project template** (a whole project). The names of the folders are found
without regard to case. Every folder directly below `Code` or `Project` is one template; its name is the title.

## A template folder

```
Templates/Code/Fire Class/
    $name$.script
    template.json            (optional)
```

```
class $class$ {
}
```

The files are copied to where the file or project is made. **Placeholders** in the names of files and folders and in the text are replaced:

| placeholder | value for the name `my-thing` |
|---|---|
| `$name$` | `my-thing` (as typed) |
| `$ident$` | `my_thing` (letters, digits and `_`; a name that does not start with a letter gets an `L`) |
| `$class$` | `My_thing` (the identifier with a capital first letter) |
| `$identlower$` | `my_thing` |
| `$project$`, `$projectident$` | the name of the project that a code template is added to (the project itself for a project template), and as an identifier |
| `$year$`, `$date$` | `2026`, `2026-10-09` |

Every placeholder can also be written `__name__` (`__ident__`, ...) for a file system or a tool that dislikes `$` in names. Anything else between the signs stays as it is (`__init__`). Binary files
(a file with a zero byte) are copied unchanged. A file that exists already is never overwritten: nothing is written then, and the editor says which one it is. A name that would leave the
folder is refused.

**`template.json`** is optional - a folder without it is a template with the folder name as its title, no description and a default icon. All fields are optional:

```json
{
  "title": "Fire Class",
  "description": "A script with an empty class that is named like the file.",
  "icon": "class",
  "defaultName": "MyClass",
  "extension": ".script",
  "open": [ "$name$.script" ],
  "order": 20,
  "hidden": false
}
```

| field | meaning |
|---|---|
| `title`, `description` | what the list shows |
| `icon` | a picture in the folder (`icon.png` and `template.png` are found without naming them) or the key of a built-in symbol: `file script class window view project console desktop library native empty markdown image package` |
| `defaultName` | the name that is proposed (default: the title without spaces) |
| `extension` | the extension that is shown behind the name (default: the one of the first file with `$name$` in its name) |
| `open` | the files that the editor opens afterwards (default: the first file of a code template, the first script of a project) |
| `order` | sorting in the list (smaller first, default 100), then by title |
| `hidden` | the template does not show up |
| `empty` | a project template that makes no project (the "Empty" solution) |
| `type` | `exe` or `library`, for a project template without a project file of its own |
| `references` | packages that the project made from it references |
| `addPackageReference` | `false`: a project made from the template of a package does not reference that package (it does by default) |

## Project templates

A project template makes the folder `{location}/{name}` and puts its files there. It should bring a project file, usually `$name$.fireproj`:

```json
{
  "format": 1,
  "name": "$name$",
  "type": "exe"
}
```

Without one, a project file `{name}.fireproj` is made (`"type": "library"` in `template.json` makes a library). A project made from the template **of a package** gets a reference to that
package (`Package` and `Version`), so that what the template uses is there - a desktop template that comes with a UI package builds at once.

## Templates in a package

A package brings templates with a folder **`templates/`** (with `Code/` and `Project/` below it), next to the forge file of `ember forge` - like `native/` next to the project of a library:

```
mathkit/
    package.json
    mathkit.fire
    templates/
        Code/Vector Class/$name$.script
        Project/Math Demo/$name$.fireproj
        Project/Math Demo/main.script
```

* `ember forge` puts the folder into the package (`templates/...`; the `templates` field of `package.json` can name another folder instead).
* A library **project** that has a folder `templates/` packs it: *Pack as Package* takes it along. The folder is not code of the project (the default file patterns leave it out), but it shows up in the
  solution explorer as content.
* `ember install` unpacks the package, and its templates are in the lists of the editor from then on, marked `From mathkit 1.0.0`. They are removed with the package.

## Editing templates

*File > Templates* in the editor:

* **Open Template...** - the flat list of all templates (code and project, the program's, yours and those of packages); the files of the chosen one (and its `template.json`) open in the **plain text
  editor**. It is tied to no project and does no checks, so `$name$` is no error; the colours follow the extension (fire for `.script`, markup for `.fxml`, C++ for headers and sources; a placeholder
  counts as a name). A template of the program folder or of a package is not yours to change in place (the folder may be read-only, and an update would overwrite it), so the editor offers a copy first.
* **Copy Template to My Templates...** - copies the folder of any template to `~/spark/templates/Code/{title}` or `.../Project/{title}`, with ` (copy)` added to the title so that both can be told
  apart in the list, and opens the copy. Change the title in the `template.json` when you are done.
* **Open My Templates Folder** - the folder `~/spark/templates` in the file manager (made, with `Code` and `Project`, when it is not there yet). A template that you make there by hand shows up the
  next time a list is opened - there is nothing to register.

## Samples (Help > Samples...)

The sample projects that ship with fire live in `src/fire.Project/Samples/<Name>/` (copied to `Samples\` next to the editor): an ordinary project folder (`<Name>.fireproj`, the scripts) plus an optional `sample.json`
(`title`, `description`, `open` = the files to open afterwards, `order`). **Help > Samples...** lists them; *Open* copies the chosen sample (without `sample.json`) into a folder of your choice - proposed:
`~/spark/samples/<Name>`, which must not exist or be empty - and opens the copy as a project, so the shipped files stay untouched. `SampleCatalog` (fire.Project) does the finding and copying.
To add a sample, add a folder with a project file; the tests (ProjectTests) run the console samples in the VM. Present: HelloWorld, Classes, Ownership, Threads (console), Graphics, UI (window).
