# The pictures of the documentation

Most pictures are made from the examples of the pages themselves:

```bash
python3 extract.py          # writes src/NAME.fire (and src/NAME/*.fxml) for every picture that stands right before an example
./make.sh                   # runs every program in src/ and writes NAME.png here (all, or: ./make.sh ui-lists ui-menus)
```

`extract.py` explains the rules (a picture before a `fire` block, or before an `xml` block of a `.fxml` file and its code-behind; `<!-- shot: ... -->`
after the example runs code before the picture is taken). Programs in `src/` without the "Made from" line are written by hand (a dialog, a 3D scene, the
circuit view). `src/shot.fire` saves the picture.

The pictures in the repository were made on Linux under Xvfb with the font Noto Sans (`~/.config/kdeglobals`: `font=NotoSans-Regular,10`,
`XDG_CURRENT_DESKTOP=KDE`). The `spark-*.png` pictures are screenshots of spark (`xvfb-run`, `import -window root`, cropped to the window).
