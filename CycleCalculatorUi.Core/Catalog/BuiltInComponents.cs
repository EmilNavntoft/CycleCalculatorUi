using CycleCalculator.CycleModel.Fluids;
using CycleCalculator.CycleModel.Model;
using CycleCalculator.CycleModel.Model.Connectors;
using CycleCalculator.CycleModel.Model.En12900;
using CycleCalculatorUi.Core.Document;

namespace CycleCalculatorUi.Core.Catalog;

/// <summary>
/// Descriptors for the backend components. Port keys match the backend property names
/// (Inlet, Outlet, Outlet2…), and all values use the backend units: bar, °C, kJ/kg, kg/s.
/// </summary>
public static class BuiltInComponents
{
    public static IEnumerable<ComponentDescriptor> All() =>
    [
        BoundaryCondition(),
        TemperatureBoundary(),
        SimpleCompressor(),
        En12900Compressor(),
        ExpansionValve(),
        ThreeWayValve(),
        FixedEfficiencyEjector(),
        FixedEfficiencyHeatExchanger(),
        FixedUaHeatExchanger(),
        FixedUaEvaporator(),
        Split(),
        Merge(),
        Receiver()
    ];

    private static bool ChoiceIs(NodeModel node, string key, params string[] values) =>
        node.Choices.TryGetValue(key, out string? value) && values.Contains(value);

    public static ComponentDescriptor BoundaryCondition() => new()
    {
        TypeId = nameof(CycleCalculator.CycleModel.Model.BoundaryCondition),
        DisplayName = "Boundary condition",
        Category = Categories.Boundaries,
        NamePrefix = "BC",
        Description = "Single-port source, sink or fixed state. Fixes any of mass flow, pressure and " +
                      "temperature or enthalpy; values left empty are solved for.",
        Ports = [new PortDescriptor("Port", "Port", PortKind.Fluid, PortDirection.Undefined, 0, 1, 0.5)],
        Parameters =
        [
            new ParameterDescriptor("massFlow", "Mass flow", ParameterKind.Number)
            {
                Unit = "kg/s", Optional = true,
                Help = "Positive when flow enters the boundary (a sink). A source supplying 0.5 kg/s uses -0.5."
            },
            new ParameterDescriptor("pressure", "Pressure", ParameterKind.Number)
            {
                Unit = "bar", Optional = true, Min = 0, MinExclusive = true
            },
            new ParameterDescriptor("thermal", "Thermal state", ParameterKind.Choice)
            {
                DefaultChoice = "none",
                Options =
                [
                    new ChoiceOption("none", "Free"),
                    new ChoiceOption("temperature", "Fixed temperature"),
                    new ChoiceOption("enthalpy", "Fixed enthalpy")
                ]
            },
            new ParameterDescriptor("temperature", "Temperature", ParameterKind.Number)
            {
                Unit = "°C", DefaultNumber = 20, Min = -273.15, MinExclusive = true,
                IsActive = node => ChoiceIs(node, "thermal", "temperature")
            },
            new ParameterDescriptor("enthalpy", "Enthalpy", ParameterKind.Number)
            {
                Unit = "kJ/kg", DefaultNumber = 400,
                IsActive = node => ChoiceIs(node, "thermal", "enthalpy")
            }
        ],
        Create = c => new BoundaryCondition(c.Name, c.Fluid(), c.Backend(),
            c.OptionalNumber("massFlow"), c.OptionalNumber("pressure"),
            c.OptionalNumber("temperature"), c.OptionalNumber("enthalpy")),
        GetPort = Typed.Ports<BoundaryCondition>(("Port", b => b.Port))
    };

    public static ComponentDescriptor TemperatureBoundary() => new()
    {
        TypeId = nameof(CycleCalculator.CycleModel.Model.TemperatureBoundary),
        DisplayName = "Fixed outlet temperature",
        Category = Categories.HeatExchangers,
        NamePrefix = "TB",
        Description = "Ideal heater or cooler that brings the flow to a fixed outlet temperature at constant pressure.",
        Ports = Inline(),
        Parameters =
        [
            new ParameterDescriptor("outletTemperature", "Outlet temperature", ParameterKind.Number)
            {
                Unit = "°C", DefaultNumber = 30, Min = -273.15, MinExclusive = true
            }
        ],
        Create = c => new TemperatureBoundary(c.Name, c.Fluid(), c.Backend(), c.Number("outletTemperature")),
        GetPort = Typed.Ports<TemperatureBoundary>(("Inlet", t => t.Inlet), ("Outlet", t => t.Outlet))
    };

    public static ComponentDescriptor SimpleCompressor() => new()
    {
        TypeId = nameof(CycleCalculator.CycleModel.Model.SimpleCompressor),
        DisplayName = "Compressor",
        Category = Categories.Compressors,
        NamePrefix = "Comp",
        Description = "Compressor with a fixed isentropic efficiency and discharge pressure.",
        Ports = Inline(),
        Parameters =
        [
            new ParameterDescriptor("isentropicEfficiency", "Isentropic efficiency", ParameterKind.Number)
            {
                Unit = "-", DefaultNumber = 0.7, Min = 0, MinExclusive = true, Max = 1
            },
            new ParameterDescriptor("dischargePressure", "Discharge pressure", ParameterKind.Number)
            {
                Unit = "bar", DefaultNumber = 90, Min = 0, MinExclusive = true
            },
            new ParameterDescriptor("massFlow", "Mass flow", ParameterKind.Number)
            {
                Unit = "kg/s", Optional = true, Min = 0,
                Help = "Leave empty to let the rest of the system determine the flow."
            }
        ],
        Create = c => new SimpleCompressor(c.Name, c.Fluid(), c.Backend(),
            c.Number("isentropicEfficiency"), c.Number("dischargePressure"), c.OptionalNumber("massFlow")),
        GetPort = Typed.Ports<SimpleCompressor>(("Inlet", s => s.Inlet), ("Outlet", s => s.Outlet))
    };

    public static ComponentDescriptor ExpansionValve()
    {
        static bool Controlled(NodeModel node) => ChoiceIs(node, "mode", "temperature", "superheat");

        return new ComponentDescriptor
        {
            TypeId = nameof(CycleCalculator.CycleModel.Model.ExpansionValve),
            DisplayName = "Expansion valve",
            Category = Categories.Valves,
            NamePrefix = "EXV",
            Description = "Isenthalpic expansion. The outlet pressure is fixed, controlled to a temperature or " +
                          "superheat measured through the signal input, or left free.",
            Ports =
            [
                .. Inline(),
                new PortDescriptor("Signal", "Measurement", PortKind.SignalInput, PortDirection.Undefined, 0, 0.5, 0)
                {
                    IsRequired = Controlled
                }
            ],
            Parameters =
            [
                new ParameterDescriptor("mode", "Control mode", ParameterKind.Choice)
                {
                    DefaultChoice = "fixed",
                    Options =
                    [
                        new ChoiceOption("fixed", "Fixed outlet pressure"),
                        new ChoiceOption("temperature", "Temperature setpoint"),
                        new ChoiceOption("superheat", "Superheat setpoint"),
                        new ChoiceOption("free", "Free (set by other components)")
                    ]
                },
                new ParameterDescriptor("outletPressure", "Outlet pressure", ParameterKind.Number)
                {
                    Unit = "bar", DefaultNumber = 35, Min = 0, MinExclusive = true,
                    IsActive = node => ChoiceIs(node, "mode", "fixed")
                },
                new ParameterDescriptor("temperatureSetpoint", "Temperature setpoint", ParameterKind.Number)
                {
                    Unit = "°C", DefaultNumber = 0, Min = -273.15, MinExclusive = true,
                    Help = "Temperature of the measured port that the valve controls to.",
                    IsActive = node => ChoiceIs(node, "mode", "temperature")
                },
                new ParameterDescriptor("superheatSetpoint", "Superheat setpoint", ParameterKind.Number)
                {
                    Unit = "K", DefaultNumber = 5,
                    Help = "Superheat of the measured port that the valve controls to.",
                    IsActive = node => ChoiceIs(node, "mode", "superheat")
                },
                new ParameterDescriptor("minOutletPressure", "Min. outlet pressure", ParameterKind.Number)
                {
                    Unit = "bar", Optional = true, Min = 0, MinExclusive = true, IsActive = Controlled
                },
                new ParameterDescriptor("maxOutletPressure", "Max. outlet pressure", ParameterKind.Number)
                {
                    Unit = "bar", Optional = true, Min = 0, MinExclusive = true, IsActive = Controlled
                }
            ],
            Create = c => new ExpansionValve(c.Name, c.Fluid(), c.Backend(),
                outletPressure: c.OptionalNumber("outletPressure"),
                temperatureSetpoint: c.OptionalNumber("temperatureSetpoint"),
                minOutletPressure: c.OptionalNumber("minOutletPressure"),
                maxOutletPressure: c.OptionalNumber("maxOutletPressure"),
                superheatSetpoint: c.OptionalNumber("superheatSetpoint")),
            GetPort = Typed.Ports<ExpansionValve>(("Inlet", v => v.Inlet), ("Outlet", v => v.Outlet)),
            GetSignalPort = Typed.SignalPorts<ExpansionValve>(("Signal", v => v.SignalPort)),
            ReadOutputs = Typed.Outputs<ExpansionValve>(v =>
            [
                new OutputValue("Outlet pressure", v.OutletPressure, "bar"),
                new OutputValue("Temperature error", v.TemperatureError, "K"),
                new OutputValue("Superheat error", v.SuperheatError, "K")
            ])
        };
    }

    public static ComponentDescriptor Split() => new()
    {
        TypeId = nameof(CycleCalculator.CycleModel.Model.Split),
        DisplayName = "Split",
        Category = Categories.Flow,
        NamePrefix = "Split",
        Description = "Divides one flow into two at the same state. The flow ratio is set by the rest of the system.",
        Ports =
        [
            new PortDescriptor("Inlet", "Inlet", PortKind.Fluid, PortDirection.Inlet, 0, 0, 0.5),
            new PortDescriptor("Outlet", "Outlet 1", PortKind.Fluid, PortDirection.Outlet, 0, 1, 0.5),
            new PortDescriptor("Outlet2", "Outlet 2", PortKind.Fluid, PortDirection.Outlet, 0, 0.5, 1)
        ],
        Create = c => new Split(c.Name, c.Fluid(), c.Backend()),
        GetPort = Typed.Ports<Split>(("Inlet", s => s.Inlet), ("Outlet", s => s.Outlet), ("Outlet2", s => s.Outlet2))
    };

    public static ComponentDescriptor Merge() => new()
    {
        TypeId = nameof(CycleCalculator.CycleModel.Model.Merge),
        DisplayName = "Merge",
        Category = Categories.Flow,
        NamePrefix = "Merge",
        Description = "Adiabatic mixing of two flows at the same pressure.",
        Ports =
        [
            new PortDescriptor("Inlet", "Inlet 1", PortKind.Fluid, PortDirection.Inlet, 0, 0, 0.5),
            new PortDescriptor("Inlet2", "Inlet 2", PortKind.Fluid, PortDirection.Inlet, 0, 0.5, 1),
            new PortDescriptor("Outlet", "Outlet", PortKind.Fluid, PortDirection.Outlet, 0, 1, 0.5)
        ],
        Create = c => new Merge(c.Name, c.Fluid(), c.Backend()),
        GetPort = Typed.Ports<Merge>(("Inlet", m => m.Inlet), ("Inlet2", m => m.Inlet2), ("Outlet", m => m.Outlet))
    };

    public static ComponentDescriptor Receiver() => new()
    {
        TypeId = nameof(CycleCalculator.CycleModel.Model.Receiver),
        DisplayName = "Receiver",
        Category = Categories.Flow,
        NamePrefix = "Rec",
        Description = "Separates a two-phase inlet into saturated gas and saturated liquid outlets.",
        Ports =
        [
            new PortDescriptor("Inlet", "Inlet", PortKind.Fluid, PortDirection.Inlet, 0, 0, 0.5),
            new PortDescriptor("OutletGas", "Gas outlet", PortKind.Fluid, PortDirection.Outlet, 0, 1, 0.25),
            new PortDescriptor("OutletLiquid", "Liquid outlet", PortKind.Fluid, PortDirection.Outlet, 0, 1, 0.75)
        ],
        Create = c => new Receiver(c.Name, c.Fluid(), c.Backend()),
        GetPort = Typed.Ports<Receiver>(("Inlet", r => r.Inlet), ("OutletGas", r => r.OutletGas),
            ("OutletLiquid", r => r.OutletLiquid))
    };

    public static ComponentDescriptor En12900Compressor()
    {
        static bool DischargeIsPressure(NodeModel node) => ChoiceIs(node, "dischargeCondition", "pressure");

        string[] terms = ["c1 · 1", "c2 · S", "c3 · D", "c4 · S²", "c5 · S·D", "c6 · D²", "c7 · S³", "c8 · S²·D", "c9 · S·D²", "c10 · D³"];
        const string polynomialHelp = "Ten EN 12900 coefficients: S is the suction saturation temperature (°C), " +
                                      "D the discharge variable. Paste a row or column from a spreadsheet.";

        return new ComponentDescriptor
        {
            TypeId = nameof(CycleCalculator.CycleModel.Model.En12900Compressor),
            DisplayName = "Compressor (EN 12900)",
            Category = Categories.Compressors,
            NamePrefix = "Comp",
            Description = "Compressor driven by EN 12900 mass flow and power polynomials. The mass flow is " +
                          "corrected from the map's nominal superheat to the actual suction density.",
            Ports = Inline(),
            Parameters =
            [
                new ParameterDescriptor("dischargeCondition", "Discharge condition", ParameterKind.Choice)
                {
                    DefaultChoice = "pressure",
                    Options =
                    [
                        new ChoiceOption("pressure", "Fixed discharge pressure"),
                        new ChoiceOption("saturationTemperature", "Fixed discharge saturation temperature")
                    ]
                },
                new ParameterDescriptor("dischargePressure", "Discharge pressure", ParameterKind.Number)
                {
                    Unit = "bar", DefaultNumber = 110, Min = 0, MinExclusive = true, IsActive = DischargeIsPressure
                },
                new ParameterDescriptor("dischargeSaturationTemperature", "Discharge sat. temperature", ParameterKind.Number)
                {
                    Unit = "°C", DefaultNumber = 40, Min = -273.15, MinExclusive = true,
                    IsActive = node => !DischargeIsPressure(node)
                },
                new ParameterDescriptor("dischargeVariable", "Polynomial discharge variable (D)", ParameterKind.Choice)
                {
                    DefaultChoice = nameof(En12900DischargeVariable.Pressure),
                    Options =
                    [
                        new ChoiceOption(nameof(En12900DischargeVariable.Pressure), "Discharge pressure (bar abs.)"),
                        new ChoiceOption(nameof(En12900DischargeVariable.SaturationTemperature), "Discharge saturation temperature (°C)")
                    ]
                },
                new ParameterDescriptor("nominalSuperheat", "Nominal suction superheat", ParameterKind.Number)
                {
                    Unit = "K", DefaultNumber = 10, Min = 0,
                    Help = "Suction superheat at which the polynomials were measured."
                },
                new ParameterDescriptor("massFlowUnit", "Mass flow polynomial unit", ParameterKind.Choice)
                {
                    DefaultChoice = nameof(En12900MassFlowUnit.KilogramsPerHour),
                    Options =
                    [
                        new ChoiceOption(nameof(En12900MassFlowUnit.KilogramsPerHour), "kg/h"),
                        new ChoiceOption(nameof(En12900MassFlowUnit.KilogramsPerSecond), "kg/s")
                    ]
                },
                new ParameterDescriptor("massFlowPolynomial", "Mass flow polynomial", ParameterKind.Coefficients)
                {
                    Length = 10, CoefficientLabels = terms, Help = polynomialHelp,
                    DefaultCoefficients =
                    [
                        2396.20919901144, 69.4448899794199, -5.00691806645477, 0.899339507043113, -0.00410373098918049,
                        -0.00457899885315599, 0.00873555952226035, 0.00163967376135643, -0.000277241052561197,
                        -0.00000000165694956689
                    ]
                },
                new ParameterDescriptor("powerUnit", "Power polynomial unit", ParameterKind.Choice)
                {
                    DefaultChoice = nameof(En12900PowerUnit.Watts),
                    Options =
                    [
                        new ChoiceOption(nameof(En12900PowerUnit.Watts), "W"),
                        new ChoiceOption(nameof(En12900PowerUnit.Kilowatts), "kW")
                    ]
                },
                new ParameterDescriptor("powerPolynomial", "Power polynomial", ParameterKind.Coefficients)
                {
                    Length = 10, CoefficientLabels = terms, Help = polynomialHelp,
                    DefaultCoefficients =
                    [
                        -15179.3465346534, -464.76089108911, 754.703465346531, -4.01655742574256, 1.45824059405942,
                        -3.04439702970294, 0.0102332673267327, -0.0566240792079209, 0.0384600297029703, 0.00535540990099
                    ]
                },
                new ParameterDescriptor("currentPolynomial", "Current polynomial (A)", ParameterKind.Coefficients)
                {
                    Length = 10, CoefficientLabels = terms, Optional = true,
                    Help = "Optional. Leave empty if the current is not needed."
                }
            ],
            Create = c =>
            {
                double superheat = c.Number("nominalSuperheat");
                double[]? current = c.OptionalCoefficients("currentPolynomial");
                return new En12900Compressor(c.Name, c.Fluid(), c.Backend(),
                    new En12900Polynomial(c.Coefficients("massFlowPolynomial"), superheat),
                    new En12900Polynomial(c.Coefficients("powerPolynomial"), superheat),
                    Enum.Parse<En12900MassFlowUnit>(c.Choice("massFlowUnit")),
                    Enum.Parse<En12900PowerUnit>(c.Choice("powerUnit")),
                    Enum.Parse<En12900DischargeVariable>(c.Choice("dischargeVariable")),
                    c.Choice("dischargeCondition") == "pressure"
                        ? CompressorDischargeCondition.AtPressure(c.Number("dischargePressure"))
                        : CompressorDischargeCondition.AtSaturationTemperature(c.Number("dischargeSaturationTemperature")),
                    current is null ? null : new En12900Polynomial(current, superheat));
            },
            GetPort = Typed.Ports<En12900Compressor>(("Inlet", s => s.Inlet), ("Outlet", s => s.Outlet)),
            ReadOutputs = Typed.Outputs<En12900Compressor>(compressor =>
            {
                En12900CompressorPerformance performance =
                    compressor.CalculatePerformance(compressor.Inlet.Pressure, compressor.Inlet.Enthalpy);
                return
                [
                    new OutputValue("Power", performance.PowerKilowatts, "kW"),
                    new OutputValue("Suction sat. temperature", performance.SuctionSaturationTemperature, "°C"),
                    new OutputValue("Discharge sat. temperature", performance.DischargeSaturationTemperature, "°C"),
                    new OutputValue("Current", performance.CurrentAmperes, "A")
                ];
            })
        };
    }

    public static ComponentDescriptor ThreeWayValve()
    {
        static bool Controlled(NodeModel node) => ChoiceIs(node, "mode", "temperature", "massFlow");

        return new ComponentDescriptor
        {
            TypeId = nameof(CycleCalculator.CycleModel.Model.ThreeWayValve),
            DisplayName = "Three-way valve",
            Category = Categories.Valves,
            NamePrefix = "3WV",
            Description = "Splits the flow between two outlets, either with a fixed opening or adjusted to " +
                          "reach a temperature or mass flow measured through the signal input.",
            Ports =
            [
                new PortDescriptor("Inlet", "Inlet", PortKind.Fluid, PortDirection.Inlet, 0, 0, 0.5),
                new PortDescriptor("Outlet", "Outlet 1", PortKind.Fluid, PortDirection.Outlet, 0, 1, 0.5),
                new PortDescriptor("Outlet2", "Outlet 2", PortKind.Fluid, PortDirection.Outlet, 0, 0.5, 1),
                new PortDescriptor("Signal", "Measurement", PortKind.SignalInput, PortDirection.Undefined, 0, 0.5, 0)
                {
                    IsRequired = Controlled
                }
            ],
            Parameters =
            [
                new ParameterDescriptor("mode", "Control mode", ParameterKind.Choice)
                {
                    DefaultChoice = "fixed",
                    Options =
                    [
                        new ChoiceOption("fixed", "Fixed opening"),
                        new ChoiceOption("temperature", "Temperature setpoint"),
                        new ChoiceOption("massFlow", "Mass flow setpoint")
                    ]
                },
                new ParameterDescriptor("fixedOpening", "Opening to outlet 1", ParameterKind.Number)
                {
                    Unit = "-", DefaultNumber = 0.5, Min = 0, Max = 1, IsActive = node => !Controlled(node),
                    Help = "Fraction of the inlet flow sent to outlet 1."
                },
                new ParameterDescriptor("temperatureSetpoint", "Temperature setpoint", ParameterKind.Number)
                {
                    Unit = "°C", DefaultNumber = 30, Min = -273.15, MinExclusive = true,
                    IsActive = node => ChoiceIs(node, "mode", "temperature")
                },
                new ParameterDescriptor("massFlowSetpoint", "Mass flow setpoint", ParameterKind.Number)
                {
                    Unit = "kg/s", DefaultNumber = 0.5, Min = 0,
                    IsActive = node => ChoiceIs(node, "mode", "massFlow"),
                    Help = "Mass flow through the measured port, counted in that port's flow direction."
                },
                new ParameterDescriptor("minimumOpening", "Minimum opening", ParameterKind.Number)
                {
                    Unit = "-", Optional = true, Min = 0, Max = 0.5, IsActive = Controlled,
                    Help = "Keeps both outlets at least this open."
                }
            ],
            Create = c => new ThreeWayValve(c.Name, c.Fluid(), c.Backend(),
                temperatureSetpoint: c.OptionalNumber("temperatureSetpoint"),
                fixedOpening: c.OptionalNumber("fixedOpening"),
                minimumOpening: c.OptionalNumber("minimumOpening"),
                massFlowSetpoint: c.OptionalNumber("massFlowSetpoint")),
            GetPort = Typed.Ports<ThreeWayValve>(("Inlet", v => v.Inlet), ("Outlet", v => v.Outlet), ("Outlet2", v => v.Outlet2)),
            GetSignalPort = Typed.SignalPorts<ThreeWayValve>(("Signal", v => v.SignalPort)),
            ReadOutputs = Typed.Outputs<ThreeWayValve>(v =>
            [
                new OutputValue("Opening to outlet 1", v.Opening, "-"),
                new OutputValue("Temperature error", v.TemperatureError, "K"),
                new OutputValue("Mass flow error", v.MassFlowError, "kg/s")
            ])
        };
    }

    public static ComponentDescriptor FixedEfficiencyEjector() => new()
    {
        TypeId = nameof(CycleCalculator.CycleModel.Model.FixedEfficiencyEjector),
        DisplayName = "Ejector",
        Category = Categories.Flow,
        NamePrefix = "Ejector",
        Description = "Two-phase ejector with a fixed isentropic efficiency. The motive flow entrains the suction flow.",
        Ports =
        [
            new PortDescriptor("InletMotive", "Motive inlet", PortKind.Fluid, PortDirection.Inlet, 0, 0, 0.5),
            new PortDescriptor("InletSuction", "Suction inlet", PortKind.Fluid, PortDirection.Inlet, 0, 0.375, 1),
            new PortDescriptor("OutletDischarge", "Discharge", PortKind.Fluid, PortDirection.Outlet, 0, 1, 0.5)
        ],
        Parameters =
        [
            new ParameterDescriptor("isentropicEfficiency", "Isentropic efficiency", ParameterKind.Number)
            {
                Unit = "-", DefaultNumber = 0.3, Min = 0, MinExclusive = true, Max = 1
            },
            new ParameterDescriptor("dischargePressure", "Discharge pressure", ParameterKind.Number)
            {
                Unit = "bar", Optional = true, Min = 0, MinExclusive = true,
                Help = "Leave empty to let the ejector determine the discharge pressure."
            }
        ],
        Create = c => new FixedEfficiencyEjector(c.Name, c.Fluid(), c.Backend(),
            c.Number("isentropicEfficiency"), c.OptionalNumber("dischargePressure")),
        GetPort = Typed.Ports<FixedEfficiencyEjector>(("InletMotive", e => e.InletMotive),
            ("InletSuction", e => e.InletSuction), ("OutletDischarge", e => e.OutletDischarge))
    };

    public static ComponentDescriptor FixedEfficiencyHeatExchanger()
    {
        static bool Controlled(NodeModel node) => ChoiceIs(node, "mode", "temperature");

        return new ComponentDescriptor
        {
            TypeId = nameof(CycleCalculator.CycleModel.Model.FixedEfficiencyHeatExchanger),
            DisplayName = "Heat exchanger (effectiveness)",
            Category = Categories.HeatExchangers,
            NamePrefix = "HX",
            Description = "Counter-current heat exchanger without pressure drop. The effectiveness is fixed, " +
                          "or adjusted so a temperature measured through the signal input reaches a setpoint.",
            Streams = ["Stream 1 (top)", "Stream 2 (bottom)"],
            Ports =
            [
                .. CounterCurrent(),
                new PortDescriptor("Signal", "Measurement", PortKind.SignalInput, PortDirection.Undefined, 0, 0.5, 0)
                {
                    IsRequired = Controlled
                }
            ],
            Parameters =
            [
                new ParameterDescriptor("mode", "Mode", ParameterKind.Choice)
                {
                    DefaultChoice = "fixed",
                    Options = [new ChoiceOption("fixed", "Fixed effectiveness"), new ChoiceOption("temperature", "Temperature setpoint")]
                },
                new ParameterDescriptor("efficiency", "Effectiveness", ParameterKind.Number)
                {
                    Unit = "-", DefaultNumber = 0.5, Min = 0, Max = 1, IsActive = node => !Controlled(node)
                },
                new ParameterDescriptor("targetTemperatureSetpoint", "Temperature setpoint", ParameterKind.Number)
                {
                    Unit = "°C", DefaultNumber = 120, Min = -273.15, MinExclusive = true, IsActive = Controlled
                },
                new ParameterDescriptor("minEfficiency", "Min. effectiveness", ParameterKind.Number)
                {
                    Unit = "-", Optional = true, Min = 0, Max = 1, IsActive = Controlled
                },
                new ParameterDescriptor("maxEfficiency", "Max. effectiveness", ParameterKind.Number)
                {
                    Unit = "-", Optional = true, Min = 0, Max = 1, IsActive = Controlled
                }
            ],
            Create = c => new FixedEfficiencyHeatExchanger(c.Name, c.Fluid(0), c.Backend(0), c.Fluid(1), c.Backend(1),
                efficiency: c.OptionalNumber("efficiency"),
                targetTemperatureSetpoint: c.OptionalNumber("targetTemperatureSetpoint"),
                maxEfficiency: c.OptionalNumber("maxEfficiency"),
                minEfficiency: c.OptionalNumber("minEfficiency")),
            GetPort = Typed.Ports<FixedEfficiencyHeatExchanger>(("Inlet", h => h.Inlet), ("Outlet", h => h.Outlet),
                ("Inlet2", h => h.Inlet2), ("Outlet2", h => h.Outlet2)),
            GetSignalPort = Typed.SignalPorts<FixedEfficiencyHeatExchanger>(("Signal", h => h.SignalPort)),
            ReadOutputs = Typed.Outputs<FixedEfficiencyHeatExchanger>(h =>
            [
                new OutputValue("Effectiveness", h.Efficiency, "-"),
                new OutputValue("Heat flow", Math.Abs(h.Inlet.MassFlow * (h.Outlet.Enthalpy - h.Inlet.Enthalpy)), "kW"),
                new OutputValue("Temperature error", h.TemperatureError, "K")
            ])
        };
    }

    public static ComponentDescriptor FixedUaHeatExchanger() => new()
    {
        TypeId = nameof(CycleCalculator.CycleModel.Model.FixedUaHeatExchanger),
        DisplayName = "Heat exchanger (UA)",
        Category = Categories.HeatExchangers,
        NamePrefix = "HX",
        Description = "Counter-current heat exchanger without pressure drop and a fixed heat transfer " +
                      "conductance UA. Handles phase change along the exchanger.",
        Streams = ["Stream 1 (top)", "Stream 2 (bottom)"],
        Ports = CounterCurrent(),
        Parameters =
        [
            new ParameterDescriptor("ua", "UA", ParameterKind.Number) { Unit = "kW/K", DefaultNumber = 20, Min = 0 }
        ],
        Create = c => new FixedUaHeatExchanger(c.Name, c.Fluid(0), c.Backend(0), c.Fluid(1), c.Backend(1), c.Number("ua")),
        GetPort = Typed.Ports<FixedUaHeatExchanger>(("Inlet", h => h.Inlet), ("Outlet", h => h.Outlet),
            ("Inlet2", h => h.Inlet2), ("Outlet2", h => h.Outlet2)),
        ReadOutputs = Typed.Outputs<FixedUaHeatExchanger>(h => [new OutputValue("Heat flow", h.HeatTransferRate, "kW")])
    };

    public static ComponentDescriptor FixedUaEvaporator() => new()
    {
        TypeId = nameof(CycleCalculator.CycleModel.Model.FixedUaEvaporator),
        DisplayName = "Evaporator (UA, secondary)",
        Category = Categories.HeatExchangers,
        NamePrefix = "Evap",
        Description = "Evaporator with a fixed UA cooling a secondary fluid between two temperatures. It solves " +
                      "the evaporating pressure, so the valve before it must leave the pressure free.",
        Streams = ["Refrigerant", "Secondary fluid"],
        StreamDefaults = [null, new FluidSelection(CoolPropFluids.Water, CoolPropBackends.HEOS)],
        Ports = Inline(),
        Parameters =
        [
            new ParameterDescriptor("ua", "UA", ParameterKind.Number) { Unit = "kW/K", DefaultNumber = 10, Min = 0 },
            new ParameterDescriptor("superheat", "Outlet superheat", ParameterKind.Number) { Unit = "K", DefaultNumber = 5, Min = 0 },
            new ParameterDescriptor("secondaryInletTemperature", "Secondary inlet temperature", ParameterKind.Number)
            {
                Unit = "°C", DefaultNumber = 12, Min = -273.15, MinExclusive = true
            },
            new ParameterDescriptor("secondaryOutletTemperature", "Secondary outlet temperature", ParameterKind.Number)
            {
                Unit = "°C", DefaultNumber = 7, Min = -273.15, MinExclusive = true
            },
            new ParameterDescriptor("secondaryPressure", "Secondary pressure", ParameterKind.Number)
            {
                Unit = "bar", DefaultNumber = 2, Min = 0, MinExclusive = true
            }
        ],
        Create = c => new FixedUaEvaporator(c.Name, c.Fluid(0), c.Backend(0), c.Fluid(1), c.Backend(1),
            c.Number("ua"), c.Number("superheat"), c.Number("secondaryInletTemperature"),
            c.Number("secondaryOutletTemperature"), c.Number("secondaryPressure")),
        GetPort = Typed.Ports<FixedUaEvaporator>(("Inlet", e => e.Inlet), ("Outlet", e => e.Outlet)),
        ReadOutputs = Typed.Outputs<FixedUaEvaporator>(e =>
        [
            new OutputValue("Evaporating pressure", e.EvaporatingPressure, "bar"),
            new OutputValue("Secondary mass flow", e.SecondaryMassFlow, "kg/s"),
            new OutputValue("Cooling capacity", Math.Abs(e.Inlet.MassFlow * (e.Outlet.Enthalpy - e.Inlet.Enthalpy)), "kW")
        ])
    };

    /// <summary>Stream 1 runs left to right along the top, stream 2 right to left along the bottom.</summary>
    private static PortDescriptor[] CounterCurrent() =>
    [
        new("Inlet", "Inlet 1", PortKind.Fluid, PortDirection.Inlet, 0, 0, 0.25),
        new("Outlet", "Outlet 1", PortKind.Fluid, PortDirection.Outlet, 0, 1, 0.25),
        new("Inlet2", "Inlet 2", PortKind.Fluid, PortDirection.Inlet, 1, 1, 0.75),
        new("Outlet2", "Outlet 2", PortKind.Fluid, PortDirection.Outlet, 1, 0, 0.75)
    ];

    /// <summary>Inlet on the left, outlet on the right.</summary>
    private static PortDescriptor[] Inline(int stream = 0) =>
    [
        new("Inlet", "Inlet", PortKind.Fluid, PortDirection.Inlet, stream, 0, 0.5),
        new("Outlet", "Outlet", PortKind.Fluid, PortDirection.Outlet, stream, 1, 0.5)
    ];
}
