# Making other mods work in multiplayer (outline)

> **Status: planned, not built.** Nothing in this document exists yet. It describes how other mods, including
> TLD Loader mods, could make their own objects work in a TLD Revamp session without anyone changing the mod's
> multiplayer code. Names and shapes below are proposals and will change when it is built.

## 1. Why this is needed

TLD Revamp shares the game's own things: cars, items, parts, building contents, doors and switches, the radio,
weather and time. It finds them through the game's save system: anything the game can save (a save script on the
object, with its save records) can be shared, because a save record already describes everything needed to build
the same object on another machine.

A mod's objects are usually invisible to that:

- objects a mod creates from its own assets have no game save script, so the session never sees them;
- objects that do use the game's save script but carry extra state in the mod's own components lose that state
  on the other machines (it isn't in any game save record);
- a mod that reacts to player input (a button, a key, a menu) runs only on the machine of the player who pressed it.

The plan: a small public API so a mod can say "this object is shared", "this is its state" and "this happened",
and the session does the rest (ownership, joiners, players far away, saving).

## 2. Principles

1. **No changes to our multiplayer code per mod.** A mod registers; it never patches or calls our internals.
2. **Same rules as the game's objects.** A registered object gets what a car or an item gets: one owner at a
   time who simulates it, copies everywhere else, hand-off when the owner leaves, a place in the far store when
   no player is near, a record for players who join later.
3. **State travels as records.** A mod describes its state as a record (a plain serialisable class), the same way
   the game describes a car part or a tank. Records are versioned and size-capped.
4. **Optional.** A mod that doesn't use the API keeps working as it does today (local only).
5. **Safe by default.** Every record and event from another player is checked (size, version, rate, value ranges)
   before the mod sees it, as our own messages are today. A broken mod can't end a session.

## 3. Registering an object type as shared

- A mod registers a **type key** (its mod id plus a type name) and a **factory**: given a record, build the
  object here (from the mod's own prefab or assets).
- When the mod creates an object of that type in a session, it hands the object to the session, which gives it a
  network id and an owner (the creating player) and sends its first record to everyone near.
- Removal goes through the session too, so the object disappears everywhere (and from the far store), not only on
  one screen.
- Objects that come from the game's own save system need only the extra-state part (section 4): the session
  already shares them.

Open questions:
- How the type key is checked at join (all players need the same mods and versions; today the mod version is
  checked, other mods are not).
- Whether a type can declare that it rides a parent (a part bolted to a car, cargo in a container), so it moves
  with the parent instead of sending its own states.

## 4. State as save records

Three kinds of state, matching what the session already does for cars and items:

| Kind | When it is sent | Example |
|---|---|---|
| **Full record** | on creation, to joiners, when a player arrives near it, after a hand-off | everything needed to build it |
| **State record** | at a fixed rate while it changes, by its owner | position, speed, a gauge value |
| **Edit record** | when a player changes something, from that player | a switch flipped, a dial turned |

- The mod provides: write record, read record, and (for state records) "has it changed".
- The session provides: rates, compression, interest (only players near get state records), the far store,
  and replay for joiners.
- Records must be deterministic to read: the same record gives the same object on every machine. Random values
  (loot, wear) are chosen once by the owner and sent, never rolled again on each machine.

## 5. Events and ownership hooks

Callbacks a mod can implement, all optional:

- **Owner changed** (became owner / lost ownership): start or stop simulating; copies should not run physics or
  game logic that changes state.
- **Edit requested** (another player used the object): the owner decides and applies; the result travels as an
  edit record. Gives the mod a single place to validate input.
- **Placed / stored**: the object came into view on this machine or went into the far store.
- **Player joined / left**: for mods with session-wide state.

And one way to send a **mod event** (a short message to everyone or to the owner) for things that aren't object
state: a sound, an effect, a chat-like notice. Events are rate-limited and size-capped.

## 6. Session-wide settings

A mod with settings that must match for everyone (rules, difficulty, spawn rates) could register them so the host's
values are sent at join and applied for the session, the way the host's physics-lock setting is today. Local-only
settings (graphics, keys) stay local.

## 7. A converter for existing TLD Loader mods

TLD Revamp already runs TLD Loader mods under BepInEx on the beta (the mod's lifecycle calls, its logger and its
config folder). Most of those mods were written for single player. A converter would help them into multiplayer
without rewriting them:

1. **Scan** a Loader mod's assembly and report what it does that matters in a session: objects it creates,
   components it adds to game objects, input it reacts to, game state it changes directly.
2. **Classify** each finding: works as is (purely local or cosmetic), works through the game's save system
   already, or needs registration (section 3) or records (section 4).
3. **Generate a starting point**: a registration and record skeleton for the mod's author to fill in, never a
   silent rewrite of someone else's code.
4. **Report**, in plain words, which features will be local only until the author adopts the API.

Out of scope: changing a mod's behaviour without its author, and mods that replace the game's own systems (another
multiplayer, another mod loader).

## 8. Testing

Every registered type would get the same two-machine checks the game's objects have: created on one machine and
seen on the other, edited by the non-owner, owner leaves (hand-off), both players far away and back (far store),
a late joiner sees it, and the session saved and loaded.

## 9. Order (when it is built)

1. Registration + full records for objects a mod creates (sections 3 and 4).
2. Edit records and ownership hooks (section 5).
3. State records with rates and interest.
4. Session-wide settings (section 6).
5. The Loader-mod scanner and report (section 7), then the skeleton generator.
