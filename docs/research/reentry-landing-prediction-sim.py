"""Sensitivity study: where does a Mk1 capsule land after a deorbit burn from LKO?

Equatorial, prograde orbit; planar motion in Kerbin's inertial frame; ground point
measured in the rotating frame. Drag model follows KSP's DragCubeList.AddSurfaceDragDirection
and PhysicsGlobals curves (Physics.cfg) for one tip face (heat shield), one tail face and four
side faces. The atmosphere is an approximation (see ATM table), so absolute downrange numbers are
indicative; the *differences* between models are the point.
"""
import math, sys, time

MU = 3.5316e12
R = 600000.0
ATM_TOP = 70000.0
ROT = 2 * math.pi / 21549.425  # rad/s
G0 = 9.80665

# Approximate stock Kerbin static pressure (kPa) and temperature (K) by altitude (m).
ATM = [
    (0, 101.325, 288), (2500, 75.0, 275), (5000, 54.0, 262), (7500, 38.0, 250),
    (10000, 26.0, 238), (15000, 11.5, 222), (20000, 5.0, 216), (25000, 2.2, 220),
    (30000, 1.0, 228), (35000, 0.47, 238), (40000, 0.22, 248), (45000, 0.105, 252),
    (50000, 0.050, 250), (55000, 0.022, 240), (60000, 0.0090, 225), (65000, 0.0030, 210),
    (70000, 0.0, 200),
]


def lerp_table(tab, x, col):
    if x <= tab[0][0]:
        return tab[0][col]
    for a, b in zip(tab, tab[1:]):
        if x <= b[0]:
            t = (x - a[0]) / (b[0] - a[0])
            if col == 1 and a[1] > 0 and b[1] > 0:  # pressure: log-linear
                return math.exp(math.log(a[1]) * (1 - t) + math.log(b[1]) * t)
            return a[col] * (1 - t) + b[col] * t
    return tab[-1][col]


def atmos(h):
    if h >= ATM_TOP:
        return 0.0, 200.0, 1.0
    p = lerp_table(ATM, h, 1) * 1000.0
    T = lerp_table(ATM, h, 2)
    rho = p * 0.0289644 / (8.314462 * T)
    a = math.sqrt(1.4 * 8.314462 * T / 0.0289644)
    return rho, T, a


def hermite(keys, x):
    """Unity AnimationCurve style evaluation (key = (t, v, in, out))."""
    if x <= keys[0][0]:
        return keys[0][1]
    if x >= keys[-1][0]:
        return keys[-1][1]
    for k0, k1 in zip(keys, keys[1:]):
        if x <= k1[0]:
            dt = k1[0] - k0[0]
            t = (x - k0[0]) / dt
            m0 = k0[3] * dt
            m1 = k1[2] * dt
            t2, t3 = t * t, t * t * t
            return ((2 * t3 - 3 * t2 + 1) * k0[1] + (t3 - 2 * t2 + t) * m0
                    + (-2 * t3 + 3 * t2) * k1[1] + (t3 - t2) * m1)


TIP = [(0, 1, 0, 0), (0.85, 1.19, 0.6960422, 0.6960422), (1.1, 2.83, 0.730473, 0.730473), (5, 4, 0, 0)]
SURF = [(0, 0.02, 0, 0), (0.85, 0.02, 0, 0), (0.9, 0.0152439, -0.07942077, -0.07942077),
        (1.1, 0.0025, -0.005279571, -0.001936768), (2, 0.002083333, -2.314833e-05, -2.314833e-05),
        (5, 0.003333333, -0.000180556, -0.000180556), (25, 0.001428571, -7.14286e-05, 0)]
TAIL = [(0, 1, 0, 0), (0.85, 1, 0, 0), (1.1, 0.25, -0.02215106, -0.02487721),
        (1.4, 0.22, -0.03391732, -0.03391732), (5, 0.15, -0.001198566, -0.001198566), (25, 0.14, 0, 0)]
MULT = [(0, 0.5, 0, 0), (0.85, 0.5, 0, 0), (1.1, 1.3, 0, -0.008100224), (2, 0.7, -0.1104858, -0.1104858),
        (5, 0.6, 0, 0), (10, 0.85, 0.02198264, 0.02198264), (14, 0.9, 0.007694946, 0.007694946), (25, 0.95, 0, 0)]
CD = [(0.05, 0.0025, 0.15, 0.15), (0.4, 0.15, 0.3963967, 0.3963967), (0.7, 0.35, 0.9066986, 0.9066986),
      (0.75, 0.45, 3.213604, 3.213604), (0.8, 0.66, 3.49833, 3.49833), (0.85, 0.8, 2.212924, 2.212924),
      (0.9, 0.89, 1.1, 1.1), (1, 1, 1, 1)]
CDPOW = [(0, 1, 0, 0.00715953), (0.85, 1.25, 0.7780356, 0.7780356), (1.1, 2.5, 0.2492796, 0.2492796), (5, 3, 0, 0)]
PRE = [(0, 4, 0, -2975.412), (0.0001, 3, -251.1479, -251.1479), (0.01, 2, -19.63584, -19.63584),
       (0.1, 1.2, -0.7846036, -0.7846036), (1, 1, 0, 0), (100, 1, 0, 0), (200, 0.82, 0, 0),
       (500, 0.86, 0.0001932119, 0.0001932119), (1000, 0.9, 1.54299e-05, 1.54299e-05), (10000, 0.95, 0, 0)]

# Mk1 pod v2 + HeatShield1 + Mk16 chute, flown shield first (PartDatabase.cfg cubes).
# (area m^2, cube Cd) per facing.
FACE_TIP = (1.27, 0.9507)    # heat shield bottom
FACE_TAIL = (0.30, 0.50)     # parachute top (rough)
FACE_SIDE = (1.06, 0.716)    # pod sides, x4
MASS = 1240.0                # kg


def face_cd(cd, mach):
    return 1.0 if cd >= 1 else hermite(CD, cd) ** hermite(CDPOW, mach)


def cda_ksp(mach, rho_v):
    """Effective C_d*A (m^2) so that force = q * CdA, per KSP's cube model."""
    m = hermite(MULT, mach)
    tip = FACE_TIP[0] * hermite(TIP, mach) * m * face_cd(FACE_TIP[1], mach)
    tail = FACE_TAIL[0] * hermite(TAIL, mach) * m * face_cd(FACE_TAIL[1], mach)
    side = 4 * FACE_SIDE[0] * hermite(SURF, mach) * m * face_cd(FACE_SIDE[1], mach)
    area_drag = tip + tail + side
    # FlightIntegrator: drag[kN] = q[kPa] * areaDrag * dragCubeMultiplier(0.1) * dragMultiplier(8) * pseudoRe
    return area_drag * 0.1 * 8 * hermite(PRE, rho_v)


def make_accel(model, scale=1.0):
    def accel(x, y, vx, vy):
        r = math.hypot(x, y)
        g = -MU / r ** 3
        ax, ay = g * x, g * y
        h = r - R
        if h < ATM_TOP and model != "vacuum":
            rho, T, a = atmos(h)
            # air velocity: subtract the rotating atmosphere (omega x r, omega along +z)
            avx, avy = vx + ROT * y, vy - ROT * x
            v = math.hypot(avx, avy)
            if v > 0 and rho > 0:
                if model == "ksp":
                    cda = cda_ksp(v / a, rho * v)
                elif model == "const_hyper":
                    cda = cda_ksp(8.0, 1000)       # one Cd*A, the hypersonic one
                elif model == "const_sub":
                    cda = cda_ksp(0.5, 1000)       # one Cd*A, the subsonic one
                elif model == "two_regime":
                    cda = cda_ksp(8.0, 1000) if v / a > 1.5 else cda_ksp(0.5, 1000)
                else:
                    raise ValueError(model)
                f = 0.5 * rho * v * v * cda * scale
                ax -= f / MASS * avx / v
                ay -= f / MASS * avy / v
        return ax, ay
    return accel


def rk4(state, acc, dt):
    x, y, vx, vy = state
    a1 = acc(x, y, vx, vy)
    a2 = acc(x + 0.5 * dt * vx, y + 0.5 * dt * vy, vx + 0.5 * dt * a1[0], vy + 0.5 * dt * a1[1])
    v2 = (vx + 0.5 * dt * a1[0], vy + 0.5 * dt * a1[1])
    a3 = acc(x + 0.5 * dt * v2[0], y + 0.5 * dt * v2[1], vx + 0.5 * dt * a2[0], vy + 0.5 * dt * a2[1])
    v3 = (vx + 0.5 * dt * a2[0], vy + 0.5 * dt * a2[1])
    a4 = acc(x + dt * v3[0], y + dt * v3[1], vx + dt * a3[0], vy + dt * a3[1])
    v4 = (vx + dt * a3[0], vy + dt * a3[1])
    return (x + dt / 6 * (vx + 2 * v2[0] + 2 * v3[0] + v4[0]),
            y + dt / 6 * (vy + 2 * v2[1] + 2 * v3[1] + v4[1]),
            vx + dt / 6 * (a1[0] + 2 * a2[0] + 2 * a3[0] + a4[0]),
            vy + dt / 6 * (a1[1] + 2 * a2[1] + 2 * a3[1] + a4[1])), 4


def euler(state, acc, dt):
    x, y, vx, vy = state
    a = acc(x, y, vx, vy)
    vx, vy = vx + a[0] * dt, vy + a[1] * dt
    return (x + vx * dt, y + vy * dt, vx, vy), 1


def fly(model, alt0=80000.0, pe=30000.0, dt=1.0, scale=1.0, step=rk4, dt_vac=10.0):
    """Burn retrograde at (alt0, angle 0) to put periapsis at pe; return ground longitude of
    impact (deg, rotating frame, measured from the burn point's ground position at burn time),
    flight time and force evaluations."""
    r0 = R + alt0
    rp = R + pe
    a = (r0 + rp) / 2
    v0 = math.sqrt(MU * (2 / r0 - 1 / a))
    state = (r0, 0.0, 0.0, v0)
    acc = make_accel(model, scale)
    t, evals = 0.0, 0
    while True:
        h = math.hypot(state[0], state[1]) - R
        d = dt if h < ATM_TOP + 1000 else dt_vac
        if h < 0:
            break
        if model == "vacuum" and t > 0 and (state[0]*state[2] + state[1]*state[3]) > 0:
            prev = state  # vacuum periapsis above ground: report the periapsis ground point
            lon_inertial = math.degrees(math.atan2(state[1], state[0])) % 360
            return lon_inertial - math.degrees(ROT * t), t, evals
        prev = state
        state, n = step(state, acc, d)
        evals += n
        t += d
        if t > 20000:
            return None
    # interpolate to h = 0
    h0 = math.hypot(prev[0], prev[1]) - R
    h1 = math.hypot(state[0], state[1]) - R
    f = h0 / (h0 - h1)
    x = prev[0] + f * (state[0] - prev[0])
    y = prev[1] + f * (state[1] - prev[1])
    t = t - d + f * d
    lon_inertial = math.degrees(math.atan2(y, x)) % 360
    lon_ground = lon_inertial - math.degrees(ROT * t)
    return lon_ground, t, evals


def km(deg):
    return deg * math.pi / 180 * R / 1000


if __name__ == "__main__":
    out = []
    ref = fly("ksp", dt=0.1)
    out.append(("reference ksp model, RK4 dt=0.1 s", ref))
    print(f"reference: impact {ref[0]:.3f} deg = {km(ref[0]):.1f} km downrange of burn, "
          f"{ref[1]:.0f} s, {ref[2]} force evals")
    for name in ["vacuum", "const_hyper", "const_sub", "two_regime"]:
        r = fly(name, dt=0.5)
        print(f"{name:12s}: error {km(r[0] - ref[0]):+8.1f} km")
    for s in [0.8, 0.9, 1.1, 1.2]:
        r = fly("ksp", dt=0.5, scale=s)
        print(f"drag x{s:.1f}  : error {km(r[0] - ref[0]):+8.1f} km")
    for dpe in [-2000, -1000, 1000, 2000]:
        r = fly("ksp", dt=0.5, pe=30000 + dpe)
        print(f"pe {30000 + dpe:5d}  : shift {km(r[0] - ref[0]):+8.1f} km")
    print("periapsis sweep (impact downrange of burn, km; vacuum minus drag):")
    for pe in [10000, 20000, 30000, 40000, 50000]:
        rv = fly("vacuum", dt=0.5, pe=pe)  # periapsis point
        rd = fly("ksp", dt=0.5, pe=pe)
        dpe = fly("ksp", dt=0.5, pe=pe + 1000)
        dd = fly("ksp", dt=0.5, pe=pe, scale=1.1)
        print(f"  pe {pe:5d}: drag {km(rd[0]):7.1f}  vacuum {km(rv[0]):7.1f}  gap {km(rv[0]-rd[0]):7.1f}"
              f"  per +1km pe {km(dpe[0]-rd[0]):+6.1f}  per +10% drag {km(dd[0]-rd[0]):+6.1f}  t_atm~{rd[1]:.0f}s")
    print("step size (RK4 / Euler), error vs reference and force evaluations in atmosphere+vacuum:")
    for st, nm in [(rk4, "rk4"), (euler, "euler")]:
        for dt in [0.1, 0.5, 1, 2, 5]:
            t0 = time.perf_counter()
            r = fly("ksp", dt=dt, step=st, dt_vac=min(dt, 10.0) if st is euler else 10.0)
            el = time.perf_counter() - t0
            print(f"  {nm:5s} dt={dt:4}: error {km(r[0]-ref[0]):+8.2f} km, evals {r[2]:6d}, python {el*1000:6.1f} ms")
