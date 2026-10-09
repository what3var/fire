## Making things cross

This project started with the intention of creating a device control suite, tied to a batch or scripting language. As I wasn't able to find a language really cool and appropriate, i started Vibe Coding my own.

I think that i can say, this has grown to a programming language now. It's compiling to native, but still embeddable in C# and in that case (or when debugging in spark) running in a VM.

As things go with vibe coding, everything escaleted quickly. But that's fine 🔥🔥. At this point its kinda half baked, but as time goes by, things evolve.

# fire 🔥

A multi-platform scripting- *and* programming language with its very own character.

Its syntax is heavily leaned towards C#, but there are things I deliberately designed differently. Some are weird, others are beautiful or simply dangerous.

Alongside with *fire* and its compiler *forge* comes *spark*, a cute, little VS-style IDE. It's built with C# (net10) and Avalonia, so you should be able to let it run on linux and osx too. 

The package manager *ember* follows its own philosophies. Packages may only contain source code. Everything will be built at once. No precompiled natives and no hidden spyware. The online package source isnt set up yet, but the offline source works.

Compiling native fire binaries works by translating the bytecode to C++. Current targets are Windows, Linux, OSX, ESP32, and pure FreeRTOS without any devicebindings. Your installed toolchain is used, as long as you have one. If youre on Windows and you arent prepared, spark will install a compiler for you, all local and portable.

You should study the other documentation available if youre interested in details. This things went farther than i planned.

Or just download it, compile it and have a look.

## Screenshots

![Welcome dialog](https://github.com/what3var/fire/blob/master/screenshots/1.png?raw=true)

