EN 12900 compressor library
===========================

Every .json file in this folder (and its subfolders) adds compressor models to the
"Model" list of the "Compressor (EN 12900)" component. The folder ships with the
program: it lives in CycleCalculatorUi.Core/Compressors in the source and is copied
next to the program on build and publish. Changes to the copied files are picked up
while the program runs. Choosing a model copies all of its data into the system, so
saved .cycle.json files keep working without the library.

A file holds one model, or several in a "models" array. Settings next to "models"
apply to every model in the file unless a model sets them itself; settings of a
model apply to its variants unless a variant sets them itself.

{
  "manufacturer": "Acme",                  // optional, shown in front of the name
  "refrigerant": "CarbonDioxide",          // optional, CoolProp name (R744, R134a, ...)
  "dischargeVariable": "pressure",         // "pressure" (bar abs.) or "saturationTemperature" (°C)
  "nominalSuperheat": 10,                  // K, suction superheat of the polynomials
  "massFlowUnit": "kg/h",                  // "kg/h" (default) or "kg/s"
  "powerUnit": "W",                        // "W" (default) or "kW"
  "nominalVoltage": 400,                   // V the current polynomials were measured at
  "models": [
    {
      "name": "AC-100",
      "variants": [
        // Fixed speed: one polynomial set, measured at the net frequency.
        { "speed": "fixed", "netFrequency": 50,
          "massFlow": [c1, ..., c10], "power": [c1, ..., c10], "current": [c1, ..., c10] },
        { "speed": "fixed", "netFrequency": 60, "nominalVoltage": 460,
          "massFlow": [...], "power": [...], "current": [...] },

        // Variable speed, one set per measured frequency, interpolated between them
        // (quadratic with three or more, linear with two; extrapolated beyond them).
        { "speed": "variable", "netFrequency": 50, "scaling": "interpolate",
          "frequencies": [
            { "frequency": 30, "massFlow": [...], "power": [...], "current": [...] },
            { "frequency": 50, "massFlow": [...], "power": [...], "current": [...] },
            { "frequency": 70, "massFlow": [...], "power": [...], "current": [...] }
          ] },

        // Variable speed from one set: mass flow and power scale linearly with
        // frequency. The set is measured at netFrequency unless "frequency" is given.
        { "speed": "variable", "netFrequency": 60, "scaling": "linear",
          "massFlow": [...], "power": [...], "current": [...] }
      ]
    }
  ]
}

"scaling" is "linear" (one set) or "interpolate" (several sets); it defaults to
linear for one set and interpolate for several. "current" is optional (A); when
given, "nominalVoltage" is required and the current is scaled by nominal / net
voltage. Below the net frequency the drive lowers the voltage, so the current is
interpolated only from sets at or below the net frequency (with linear scaling it
stays constant there); above it only from sets at or above it.

A variant without "netFrequency" applies to any net frequency (give "frequency").
The older format with "massFlow", "power", "current" and "nominalFrequency" directly
on the model is still read as fixed and variable speed at that net frequency.

The ten coefficients follow EN 12900:
  c1 + c2·S + c3·D + c4·S² + c5·S·D + c6·D² + c7·S³ + c8·S²·D + c9·S·D² + c10·D³
with S the suction saturation temperature (°C) and D the discharge variable.
Comments (// ...) and trailing commas are allowed. Model names must be unique.
