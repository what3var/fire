I originally just wanted to control an old 3D printer.

Then I needed a scripting language, because GCode is a pain in the ass if you ask me.

So I tried vibe coding. Now everything's on fire. 😒

🥁 Drum roll pleeeeeease...

# 🔥 Fire

> A truly German programming language.

Fire started with a simple question:

> What would happen if a modern scripting language was developed and then reviewed by an ~~German~~ alcoholic engineer?

The answer is: **units** are **part of the language**, **exceptions** are **a matter of opinion** (resumable), **ownership** is **clearly defined**, but line endings are **not**. 🇩🇪

Why settle for just *one* decades-old tradition when you can have several?

---

## What is Fire?

Fire is an object-oriented scripting language featuring:

- 📏 built-in units
- 🧩 dynamic typing
- 🏗️ classes, inheritance, and interfaces
- 🔥 multithreading via `fire`
- 🎭 actors
- ⚡ native APIs and dynamic linking
- ♻️ resumable exceptions
- 🔧 ownership-based memory management

[All features](docs/FEATURES.md)
[The spec](docs/SPEC.md)

The goal was never to be small or particularly academic.

The goal was to create a scripting language with genuinely interesting features while shamelessly copying the best parts of C#. At the same time, garbage collection was replaced with an experimental ownership model that clearly defines object lifetimes and guarantees a predictable memory footprint.

---

## German Characteristics™

### 📏 Ordnung muss sein

Units are part of the language.

Meters are meters. Kilograms are kilograms. And if someone tries to add both together, Fire will file a polite but firmly worded complaint.

```fire
var length = 500mm
var distance = 2m

print(length + distance)
```

Fire understands that both values describe the same physical dimension and performs any necessary conversions automatically.

Invalid combinations don't silently produce nonsense.

They produce an exception.

```fire
var weight = 5kg
var length = 20cm

print(weight + length)   // nope 😐
print(weight + length!)  // But I want to!
```

Fire tries to prevent nonsense.

*Tries.*

---

### 🏛️ Tradition Matters

We appreciate innovation.

Just not too much.

That's why Fire accepts both:

```fire
var a = 1;
var b = 2;
```

and:

```fire
var a = 1
var b = 2
```

C and BASIC spent decades fighting over who was right.

Fire says:

> Both.

Because why settle for exactly *one* decades-old tradition when you can have both?

---

### 🗂️ Bureaucracy Reduction Program

Of course we love precision.

The same way we love clear definitions.

But we also enjoy going home early.

That's why Fire brings back an old friend:

```fire
with player {
    .health = .health - 10
    .gold = .gold + 100
    .LevelUp()
}
```

Surprise 🎉

Yes, that's actually `with`.

Other languages removed it, hid it, or only speak about it behind closed doors. Then they do things like this:

```csharp
invoice.customer.address.city = "Berlin";
invoice.customer.address.zipCode = "10115";
invoice.customer.address.street = "Example Street 42";
```

Fire:

```fire
with invoice.customer.address {
    .city = "Berlin"
    .zipCode = "10115"
    .street = "Example Street 42"
}
```

Fire says:

> If we already know which object we're going to work on for the next ten lines, why should we type its name ten times?

---

### 🔧 Property Comes With Responsibility

We like sharing.

But we're not communists.

That's why every object in Fire has exactly one owner.

And if ownership changes, let's do it properly:

```fire
bag.TakeTo(what3var)
```

Now give me my bag back!

Ownership controls lifetime, cleanup, and responsibilities inside the object graph.

Because somebody has to keep track of who currently owns the thing.

---

### 🔥 This Is Fine

Even when things are on fire, we like to stay in control.

Who else is supposed to supervise whether the building is burning down correctly? You?

Control requires power.

And power requires tools.

Let me introduce our good old friend:

```fire
catch (e) {
    e.resume()
}
```

A `resume` for (almost) every occasion!

Why immediately give up when an error occurs if you can inspect it, modify it, and continue exactly where things exploded?

Most programming languages treat exceptions like emergencies.

Fire treats them like an application for a special permit.

---

### 🚂 Orderly Parallelism

Other languages hand the same data to multiple threads and hope for the best.

Fire prefers controlled copies, explicit synchronization, and actors.

```fire
fire taking player {
    player.health = player.health - 10

    sync player
}
```

Data races are a fascinating concept.

Preferably in other programming languages.

---

## Why Fire?

Because programming languages have become boring.

Fire combines ideas from:

- BASIC
- C
- C++
- C#
- Python
- actor systems
- traditional scripting languages

...while adding a few ideas of its own, such as units, ownership, resumable exceptions, and controlled concurrency.

The result isn't a language trying to follow every trend.

It's a language that relentlessly follows its own ideas.

And those of its slightly insane designer.