# Live assistant demo script

A repeatable, ~3 minute demo of the live assistant: the teacher lectures, says two
things that contradict the uploaded course material, and the assistant privately flags
exactly those two and stays silent otherwise.

Pairs with [`caching-course-notes.txt`](./caching-course-notes.txt) — upload that to the
classroom first. It is the assistant's only source of truth for this demo.

## Why this material

The notes are a caching lecture built around **course-specific facts** ("in this course
the target hit rate is 85 percent") rather than general knowledge.

That is deliberate. If the material were something the model already knows — the solar
system, basic physics — it could flag a planted error from its own priors, and a card
would prove nothing about whether retrieval ran. Every fact the demo attacks exists
**only** in the uploaded file, so a card is evidence that the whole path worked:
transcript → idea boundary → retrieval → grounded evaluation → delivery.

The vocabulary is plain English on purpose: no acronyms, no file paths, no code. An
earlier session had Whisper turn `/api/notifications/device-tokens` into "Vault,
Registur, Device". "Least recently used" is spelled out everywhere for the same reason —
"LRU" transcribes unreliably.

## Before you start

1. Rebuild `knowledge-service` and `live-assistant-service`.
2. Upload `caching-course-notes.txt` to the classroom. **Wait for its indexing badge to
   reach Done** before starting the session.
3. Confirm your VPN exit IP is not one Groq blocks (see `run-services.txt` §1). If Groq
   403s there is no transcript at all, and nothing else in this demo can work.
4. Start the session as the teacher.

## How to speak it

| | |
|---|---|
| **Inside a block** | Pause no more than ~2s between sentences. Longer splits the block into two ideas. |
| **Between blocks** | Pause a **full 4–5 seconds**. Count it silently. |
| **After the last block** | Wait ~30s before ending the session — cards are still in flight. |

The between-block pause is what closes an idea: the STT consumes 1.5s finalizing the
segment, then `boundary_pause_seconds` (2.0s) has to elapse on top of that.

**An idea only closes when you start speaking again.** Going quiet at the end does
nothing, which is why block 6 exists — do not skip it.

Speak normally. Do not slow down or over-enunciate.

---

## Block 1 — correct → expect **no card**

> Okay, let's start with what a cache actually is. A cache is a small, fast store that
> keeps copies of data that would be expensive to fetch again. When the program asks for
> an item and the cache already has it, we call that a cache hit. When the item is not
> there and we have to go to the slower store, that is a cache miss.

**⏸ Pause 4–5 seconds**

## Block 2 — planted error → expect a **card**

The notes say the target hit rate is **85 percent**. You will say 55.

> Now, the number that matters most when you measure a cache is the hit rate. That is
> the share of requests that are served out of the cache. In this course, the target hit
> rate we expect you to reach is fifty five percent. Anything at or above fifty five
> percent counts as a healthy cache for our assignments.

**⏸ Pause 4–5 seconds**

## Block 3 — correct → expect **no card**

This block also spaces the two errors apart far enough to clear the 45s pacing gate.

> Let's talk about why misses happen. There are three kinds. A compulsory miss happens
> the first time you ever touch an item, because nothing has loaded it yet. A capacity
> miss happens when the cache is simply too small to hold everything the program is
> using. And a conflict miss happens when several items compete for the same slot.

**⏸ Pause 4–5 seconds**

## Block 4 — planted error → expect a **card**

You will describe least-recently-used as if it were first-in-first-out. The notes
contrast the two explicitly.

> Now for eviction. When the cache is full, something has to be removed. Our default
> policy in this course is least recently used. Least recently used means we remove
> whichever entry was inserted first, so the oldest entry by insertion time always goes,
> no matter how recently it was read.

**⏸ Pause 4–5 seconds**

## Block 5 — off-syllabus → expect **no card**

Nothing here is in the notes, so retrieval should find nothing and the brain should
never be called. This is the grounding test.

> Before we continue, a quick note about the assignment. It is due next Tuesday, and I
> want it submitted as a single file with your name at the top. My office hours this
> week moved to Wednesday afternoon, so come find me then if you are stuck on anything.

**⏸ Pause 4–5 seconds**

## Block 6 — closer → **do not skip**

This is what closes block 5's idea.

> Alright, that is everything I wanted to cover today. Take a look at the notes before
> next week, and we will pick up from the eviction policies when we meet again.

**⏸ Wait ~30 seconds, then end the session.**

---

## What success looks like

**Exactly two cards** — one for block 2, one for block 4. Blocks 1, 3, 5 and 6 producing
nothing is a pass, not a failure; the assistant is a detector, not a commentator.

Cards do not appear while you are talking. Each lands roughly 20–30s after you **start**
the block that follows it:

| Card | Lands during |
|---|---|
| Block 2's | block 3 / just after |
| Block 4's | block 5 / just after |

Four cards, or a card on block 1, means it is commenting rather than detecting. Zero
cards means walk the checklist below.

### Timing margin

Only one card is delivered per 45 seconds (`feedback_min_interval_sec`), and blocks 3
and 4 together run about 55 seconds at a normal pace. That is **10 seconds of
headroom**. Rush block 3 and block 4's card is suppressed by pacing even though the
error was detected correctly. When in doubt pause longer between blocks — never
shorter.

## Debug checklist

Walk it in this order — each step depends on the one above it.

```
docker compose logs -f live-assistant-service
```

1. **Audio is reaching the agent** — look for `audio_level_probe`. `peak_rms` must be
   clearly above `threshold` while you talk. If it is not, nothing else matters.
2. **Transcript quality** — look for `stt_debug` (`STT_DEBUG_LOG_TEXT=true` is already
   set in compose, and the Groq path honours it). You want whole sentences. Check that
   "fifty five percent" survived as a number, that "least recently used" was not
   mangled, and that there are no repeated stray words. Fragmented text means your
   in-block pauses are too long.
3. **Ideas are closing** — check the boundary trigger per idea. Expect ~6 ideas, one per
   block, closing on `PAUSE`. Many more means your block pauses are too short and blocks
   are merging; far fewer means the opposite.
4. **Retrieval** — blocks 2 and 4 must return chunks scoring ≥ `retrieval_min_score`
   (0.25). Below that the brain is never called and no card can appear; confirm the
   notes finished indexing. Block 5 *should* fall below it — that is correct behaviour.
5. **The brain** — check the evaluation stage timing and verdict. A verdict below
   `feedback_confidence_min` (0.5) is dropped before delivery.
6. **Pacing** — if block 4's card is missing but the log shows it was detected, you
   paused too briefly between blocks 2 and 4. Lengthen block 3.

## Extending the demo

Two errors is the most that fits comfortably in three minutes given the 45s pacing gate.
To add a third, insert another correct block after block 4 and put the new error after
it — errors need ~55s of speech between them, not just a longer pause.

The notes carry several more untested facts that make good targets: the 64-entry cache
size, the 10ns hit / 200ns miss costs, write-through vs write-back, and the cold-start
rule. Each is stated unambiguously in the file, so a contradiction is easy to plant and
easy to score.
