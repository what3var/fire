I originally just wanted to control an old 3D printer.

Then I needed a scripting language as GCode is a mess imho.

So I tried vibe coding.

Now it's on fire. 😒

🥁 Drum roll pleeeeeease...

# 🔥 Fire

> A truly German programming language.

Fire started with a simple question:

> What would happen if a modern scripting language was reviewed by a German engineer?

The answer is: **units are part of the language**, distances are not added together just because they're both numbers, and if you try to add kilograms to meters, someone is going to complain. 🇩🇪

At the same time, we have a hard time letting go of old habits. That's why Fire supports both classic C-style semicolons and BASIC-style newline statement endings.

```fire
var x = 10;
var y = 20;

var sum = x + y
print(sum)
```

After all, why choose exactly *one* decades-old tradition when you can have both?

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

The goal was never to be the smallest language.

Nor the most academic.

The goal was simply:

> "What kind of language would I actually enjoy using?"

---

## German Characteristics™

### 📏 Ordnung muss sein

Units are part of the language.

Meters are meters. Kilograms are kilograms. And if someone tries to add them together, Fire politely but firmly objects.

```fire
var length = 500mm
var distance = 2m

print(length + distance)
```

Fire understands that both values describe the same physical dimension and performs conversions automatically.

Invalid combinations don't silently produce nonsense.

They produce exceptions.

```fire
var weight = 5kg
var length = 20cm

print(weight + length)   // nope 😐
print(weight + length!)  // But I want to!
```

Fire tries to prevent mistakes. *tries*.

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

C and BASIC spent decades arguing over who was right.

Fire says:

> Both.

---

### 🗂️ Bureaucracy Reduction Program

Of course we love precision.

We also like being able to go home early.

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

Other languages removed it, hid it, or only talk about it behind closed doors.

And then they do things like this:

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

> If we already know which object we're working on for the next ten lines, why should we type its name ten times?

---

### 🔧 Property Comes With Responsibilities

We're happy to share.

But we're not communists.

Every object in Fire has exactly one owner.

And if ownership changes, let's do it properly:

```fire
bag.TakeTo(what3var)
```

Give me my bag back!

Ownership controls lifetime, cleanup, and responsibilities within the object graph.

Because somebody ought to know who currently owns the thing.

---

### 🔥 This Is Fine

Even when things are on fire, we like to stay in control.

Who else is going to supervise whether the building burns down correctly? You?

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

Fire treats them like a special permit application.

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
- Actor systems
- traditional scripting languages

...while adding concepts such as units, ownership, resumable exceptions, and controlled concurrency.

The result isn't a language that tries to follow every trend.

It's a language that unapologetically follows its own ideas.

---

> 🔥 Fire: German engineering, BASIC nostalgia, and controlled madness in a scripting language.