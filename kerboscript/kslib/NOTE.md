# KSLib lib_lazcalc, bundled with one patch

`lib_lazcalc.ks` is [KSLib](https://github.com/KSP-KOS/KSLib)'s LAZcalc 2.2 (MIT, `LICENSE` kept with
it, its licence header untouched). The `launch` Building Block uses it for its heading.

## The patch, and why

The original `LAZcalc` ended in `IF Azimuth <= 90 ... ELSE IF Azimuth >= 270 ...` for a launch from the
descending node (a negative inclination), with nothing after it: an azimuth of 90 to 270 made it
return nothing, and `launch` had to keep its last heading (90, east, at the start). A southward
launch (`target_inc` negative) must not fly east, so:

1. The descending-node branch now ends in `RETURN Azimuth`, so every path returns a heading.
2. The arithmetic moved into `LAZcalc_azimuth(data, lat, northSpeed)`, which takes the latitude and the
   north component of the orbital velocity instead of reading `SHIP`. `LAZcalc(data)` is now a
   one-line call to it with the ship's values, so behaviour in game is the same. This is so the
   Kerboscript tests can run the arithmetic on kOS.Safe, where there is no ship.

`launch` no longer guards against LAZcalc returning nothing. Locals added (`lat`, `northSpeed`) do not
hide kOS built-ins (Core test `KosBuiltinNames`).

Before the patch no azimuth the formula can produce (0..90 or 270..360) fell outside the two branches
for a descending launch, so the fall-through is a guard rather than a case seen in the tests; the
Kerboscript tests pin a northward and a southward heading either way.

## The diff against KSLib's file

```diff
@@ -66,8 +66,12 @@ FUNCTION LAZcalc_init {
 }.
 
-FUNCTION LAZcalc {
+// Claudia Kermin patch (see NOTE.md): LAZcalc's arithmetic, split out so it can run without a ship.
+// lat is the latitude and northSpeed the orbital velocity along north, m/s (LAZcalc passes SHIP's).
+FUNCTION LAZcalc_azimuth {
     PARAMETER
-        data. //pointer to the list created by LAZcalc_init
-    LOCAL inertialAzimuth IS ARCSIN(MAX(MIN(COS(data[0]) / COS(SHIP:LATITUDE), 1), -1)).
+        data, //pointer to the list created by LAZcalc_init
+        lat,
+        northSpeed.
+    LOCAL inertialAzimuth IS ARCSIN(MAX(MIN(COS(data[0]) / COS(lat), 1), -1)).
     LOCAL VXRot IS data[3] * SIN(inertialAzimuth) - data[2] * COS(data[1]).
     LOCAL VYRot IS data[3] * COS(inertialAzimuth).
@@ -77,8 +81,7 @@ FUNCTION LAZcalc {
 
     IF data[5] {
-        LOCAL NorthComponent IS VDOT(SHIP:VELOCITY:ORBIT, SHIP:NORTH:VECTOR).
-        IF NorthComponent > data[5] {
+        IF northSpeed > data[5] {
             SET data[4] TO "Ascending".
-        } ELSE IF NorthComponent < -data[5] {
+        } ELSE IF northSpeed < -data[5] {
             SET data[4] to "Descending".
         }.
@@ -88,14 +91,21 @@ FUNCTION LAZcalc {
     IF data[4] = "Ascending" {
         RETURN Azimuth.
-        
+    }.
+
     //Returns southerly azimuth if launching from the descending node
-    } ELSE IF data[4] = "Descending" {
-        IF Azimuth <= 90 {
-            RETURN 180 - Azimuth.
-            
-        } ELSE IF Azimuth >= 270 {
-            RETURN 540 - Azimuth.
-            
-        }.
+    IF Azimuth <= 90 {
+        RETURN 180 - Azimuth.
     }.
+    IF Azimuth >= 270 {
+        RETURN 540 - Azimuth.
+    }.
+    // Claudia Kermin patch: the original returned nothing for an azimuth of 90 to 270, which is
+    // already southward.
+    RETURN Azimuth.
+}.
+
+FUNCTION LAZcalc {
+    PARAMETER
+        data. //pointer to the list created by LAZcalc_init
+    RETURN LAZcalc_azimuth(data, SHIP:LATITUDE, VDOT(SHIP:VELOCITY:ORBIT, SHIP:NORTH:VECTOR)).
 }.
```
