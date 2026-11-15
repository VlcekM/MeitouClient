# boost unordered containers

The exe keeps many tables in boost 1.5x `unordered_map` / `unordered_set` (`ptr_bucket`, `ptr_node` in the RTTI names). Where the game shows or
consumes such a table in iteration order, Meitou reproduces that order (`Meitou.Data.BoostHashOrder`).

**Verified** (decompilation; mix64(1) = `0x5bca7c69b794f8ce`, the constant the appearance code looks category 1 up by):

- Integer keys hash to themselves, then Thomas Wang's 64-bit mix (`mix64`); the bucket is `mix64(key) & (count - 1)`. Constructors set a count
  of 16 and a maximum load factor of 1.
- The nodes form one list that iteration walks. An insert into an empty bucket puts the node first in the list; into a used bucket, first in that
  bucket's run.
- An insert that would exceed `count × max load` rehashes to the power of two (at least 4) above max(size, 1.5 × old size) / max load + 1: the
  list is walked in order; a node whose new bucket is empty stays in place, one whose bucket is used moves to the front of that bucket's run.
