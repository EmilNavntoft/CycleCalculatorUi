using CycleCalculator.CycleModel.Conversion;
using CycleCalculator.CycleModel.Fluids;
using CycleCalculator.CycleModel.Model;
using CycleCalculator.CycleModel.Model.Connectors;
using CycleCalculator.CycleModel.Model.Control;
using CycleCalculator.CycleModel.Model.En12900;
using CycleCalculator.CycleModel.Model.Pumps;
using CycleCalculatorUi.Core.Document;

namespace CycleCalculatorUi.Core.Catalog;

/// <summary>
/// Descriptors for the backend components. Port keys match the backend property names
/// (Inlet, Outlet, Outlet2…), and all values use the backend units: bar, °C, kJ/kg, kg/s.
/// </summary>
public static class BuiltInComponents
{
    public static IEnumerable<ComponentDescriptor> All(CompressorLibrary? library = null) =>
    [
        BoundaryCondition(),
        TemperatureBoundary(),
        SimpleCompressor(),
        En12900Compressor(library ?? CompressorLibrary.Empty),
        Pump(),
        ExpansionValve(),
        ThreeWayValve(),
        FixedEfficiencyEjector(),
        FixedEfficiencyHeatExchanger(),
        FixedUaHeatExchanger(),
        FixedUaEvaporator(),
        Pipe(),
        Split(),
        Merge(),
        Receiver(),
        SignalOperator()
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
        EnergyKind = EnergyKind.Heat,
        GetPort = Typed.Ports<TemperatureBoundary>(("Inlet", t => t.Inlet), ("Outlet", t => t.Outlet)),
        ReadOutputs = Typed.Outputs<TemperatureBoundary>(t =>
            [new OutputValue("Heat flow", Math.Abs(t.Inlet.MassFlow * (t.Outlet.Enthalpy - t.Inlet.Enthalpy)), "kW")])
    };

    public static ComponentDescriptor SimpleCompressor()
    {
        static bool ByVolume(NodeModel node) => ChoiceIs(node, "flowSpecification", "volumeFlow");
        static bool Controlled(NodeModel node) => ChoiceIs(node, "flowSpecification", "capacityControl");

        return new ComponentDescriptor
        {
            TypeId = nameof(CycleCalculator.CycleModel.Model.SimpleCompressor),
            DisplayName = "Compressor",
            Category = Categories.Compressors,
            NamePrefix = "Comp",
            Description = "Compressor with a fixed isentropic efficiency and discharge pressure. The flow can be " +
                          "given as a mass flow or as a suction volume flow, or adjusted to hold any value measured " +
                          "through the signal input, e.g. a pressure, a heat transfer or a pump's envelope position.",
            Ports = CompressorPorts(Controlled),
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
                new ParameterDescriptor("flowSpecification", "Flow specification", ParameterKind.Choice)
                {
                    DefaultChoice = "massFlow",
                    Options =
                    [
                        new ChoiceOption("massFlow", "Mass flow"),
                        new ChoiceOption("volumeFlow", "Suction volume flow"),
                        new ChoiceOption("capacityControl", "Capacity control (signal input)")
                    ]
                },
                new ParameterDescriptor("massFlow", "Mass flow", ParameterKind.Number)
                {
                    Unit = "kg/s", Optional = true, Min = 0, IsActive = node => !ByVolume(node) && !Controlled(node),
                    Help = "Leave empty to let the rest of the system determine the flow."
                },
                new ParameterDescriptor("suctionVolumeFlow", "Suction volume flow", ParameterKind.Number)
                {
                    Unit = "m³/h", Optional = true, Min = 0, IsActive = ByVolume,
                    Help = "Volume flow at the suction state; the mass flow follows from the suction density. " +
                           "Leave empty to let the rest of the system determine the flow."
                },
                .. SignalControlParameters(Controlled, Measurements.Pressure.Key),
                new ParameterDescriptor("minMassFlow", "Min. mass flow", ParameterKind.Number)
                {
                    Unit = "kg/s", Optional = true, Min = 0, IsActive = Controlled, Nested = true,
                    Help = "Lower limit of the controlled mass flow. Leave empty for zero."
                },
                new ParameterDescriptor("maxMassFlow", "Max. mass flow", ParameterKind.Number)
                {
                    Unit = "kg/s", Optional = true, Min = 0, MinExclusive = true, IsActive = Controlled, Nested = true,
                    Help = "Upper limit of the controlled mass flow, e.g. the compressor's full capacity. Leave empty for no limit."
                },
                ControlSignParameter(Controlled, "More capacity",
                    "E.g. a suction-side pressure falls with more capacity, a discharge-side pressure rises.")
            ],
            Create = c => new SimpleCompressor(c.Name, c.Fluid(), c.Backend(),
                c.Number("isentropicEfficiency"), c.Number("dischargePressure"), c.OptionalNumber("massFlow"),
                c.OptionalNumber("suctionVolumeFlow") / 3600,
                Controlled(c.Node)
                    ? Control(c, c.OptionalNumber("minMassFlow"), c.OptionalNumber("maxMassFlow"))
                    : null),
            EnergyKind = EnergyKind.Power,
            GetPort = Typed.Ports<SimpleCompressor>(("Inlet", s => s.Inlet), ("Outlet", s => s.Outlet)),
            GetSignalPort = Typed.SignalPorts<SimpleCompressor>(("Signal", s => s.SignalPort)),
            ReadOutputs = Typed.Outputs<SimpleCompressor>(compressor =>
            {
                double density = compressor.Inlet.Fluid.Get(UnitType.Density,
                    UnitType.Pressure, compressor.Inlet.Pressure, UnitType.Enthalpy, compressor.Inlet.Enthalpy);
                return
                [
                    new OutputValue("Suction volume flow", Math.Abs(compressor.Inlet.MassFlow) / density * 3600, "m³/h"),
                    .. ControlOutputs(compressor.CapacityControl, compressor.ControlError)
                ];
            })
        };
    }

    public static ComponentDescriptor Pump()
    {
        static bool ModeIs(NodeModel node, string mode) => ChoiceIs(node, "control", mode);
        static bool Controlled(NodeModel node) => ModeIs(node, "signal");

        string[] terms = ["c0 · 1", "c1 · Q", "c2 · Q²"];
        const string curveHelp = "Head as a polynomial in the volume flow Q (m³/h): H = c0 + c1·Q + c2·Q², in the " +
                                 "head unit above. Use c2 = 0 for a straight line.";

        return new ComponentDescriptor
        {
            TypeId = nameof(CycleCalculator.CycleModel.Model.Pump),
            DisplayName = "Pump",
            Category = Categories.Pumps,
            NamePrefix = "Pump",
            Description = "Variable-speed pump limited by an envelope: the maximum and minimum head for a given " +
                          "volume flow. The pump runs at a fixed position in the envelope, adjusts to a pressure " +
                          "setpoint or to any value measured through the signal input (staying on the nearest curve " +
                          "if it cannot reach it), or lets the system set the head. Power follows from a fixed " +
                          "efficiency. Other controllers can measure its envelope position.",
            Ports = [.. Inline(), SignalInput(Controlled, Measurements.MassFlow.Key)],
            Parameters =
            [
                new ParameterDescriptor("efficiency", "Efficiency", ParameterKind.Number)
                {
                    Unit = "-", DefaultNumber = 0.6, Min = 0, MinExclusive = true, Max = 1,
                    Help = "Shaft power = hydraulic power / efficiency. All losses heat the fluid."
                },
                new ParameterDescriptor("headUnit", "Envelope head unit", ParameterKind.Choice)
                {
                    DefaultChoice = nameof(PumpHeadUnit.Metres),
                    Options =
                    [
                        new ChoiceOption(nameof(PumpHeadUnit.Metres), "m (head of pumped fluid)"),
                        new ChoiceOption(nameof(PumpHeadUnit.Kilopascals), "kPa"),
                        new ChoiceOption(nameof(PumpHeadUnit.Bar), "bar")
                    ]
                },
                new ParameterDescriptor("maxCurve", "Maximum curve", ParameterKind.Coefficients)
                {
                    Length = 3, CoefficientLabels = terms, DefaultCoefficients = [30, 0, -0.02],
                    Help = "Highest head the pump can deliver, e.g. at full speed. " + curveHelp
                },
                new ParameterDescriptor("minCurve", "Minimum curve", ParameterKind.Coefficients)
                {
                    Length = 3, CoefficientLabels = terms, DefaultCoefficients = [10, 0, -0.02],
                    Help = "Lowest head the pump can deliver, e.g. at minimum speed. " + curveHelp
                },
                new ParameterDescriptor("control", "Control mode", ParameterKind.Choice)
                {
                    DefaultChoice = "position",
                    Options =
                    [
                        new ChoiceOption("position", "Fixed envelope position"),
                        new ChoiceOption("pressureRise", "Pressure rise setpoint"),
                        new ChoiceOption("dischargePressure", "Discharge pressure setpoint"),
                        new ChoiceOption("signal", "Setpoint (signal input)"),
                        new ChoiceOption("free", "Free (head set by other components)")
                    ]
                },
                new ParameterDescriptor("position", "Envelope position", ParameterKind.Number)
                {
                    Unit = "-", DefaultNumber = 1, Min = 0, Max = 1,
                    Help = "0 runs on the minimum curve, 1 on the maximum curve; in between the head is interpolated.",
                    IsActive = node => ModeIs(node, "position")
                },
                new ParameterDescriptor("pressureRiseSetpoint", "Pressure rise setpoint", ParameterKind.Number)
                {
                    Unit = "bar", DefaultNumber = 1.5, Min = 0,
                    IsActive = node => ModeIs(node, "pressureRise")
                },
                new ParameterDescriptor("dischargePressureSetpoint", "Discharge pressure setpoint", ParameterKind.Number)
                {
                    Unit = "bar", DefaultNumber = 3, Min = 0, MinExclusive = true,
                    IsActive = node => ModeIs(node, "dischargePressure")
                },
                .. SignalControlParameters(Controlled, Measurements.MassFlow.Key, (Measurements.Temperature, 20)),
                new ParameterDescriptor("minPosition", "Min. envelope position", ParameterKind.Number)
                {
                    Unit = "-", Optional = true, Min = 0, Max = 1, IsActive = Controlled, Nested = true,
                    Help = "Lower limit of the controlled envelope position. Leave empty for the minimum curve."
                },
                new ParameterDescriptor("maxPosition", "Max. envelope position", ParameterKind.Number)
                {
                    Unit = "-", Optional = true, Min = 0, Max = 1, IsActive = Controlled, Nested = true,
                    Help = "Upper limit of the controlled envelope position. Leave empty for the maximum curve."
                },
                ControlSignParameter(Controlled, "A higher envelope position",
                    "E.g. a flow measured in the pump's own loop rises with the envelope position."),
                new ParameterDescriptor("volumeFlow", "Volume flow", ParameterKind.Number)
                {
                    Unit = "m³/h", Optional = true, Min = 0,
                    Help = "Volume flow at the inlet. Leave empty to let the rest of the system determine the flow."
                }
            ],
            Create = c =>
            {
                PumpEnvelope envelope;
                try
                {
                    envelope = new PumpEnvelope(c.Coefficients("maxCurve"), c.Coefficients("minCurve"),
                        Enum.Parse<PumpHeadUnit>(c.Choice("headUnit")));
                }
                catch (ArgumentException ex) when (ex.ParamName is "maxCurve" or "minCurve")
                {
                    throw new ParameterException(ex.ParamName, CleanMessage(ex));
                }

                PumpControl control = c.Choice("control") switch
                {
                    "pressureRise" => PumpControl.PressureRise(c.Number("pressureRiseSetpoint")),
                    "dischargePressure" => PumpControl.DischargePressure(c.Number("dischargePressureSetpoint")),
                    "free" => PumpControl.Free,
                    "signal" => PumpControl.BySignal(Control(c, c.OptionalNumber("minPosition"), c.OptionalNumber("maxPosition"))),
                    _ => PumpControl.AtPosition(c.Number("position"))
                };
                return new Pump(c.Name, c.Fluid(), c.Backend(), envelope, c.Number("efficiency"), control,
                    c.OptionalNumber("volumeFlow"));
            },
            EnergyKind = EnergyKind.Power,
            GetPort = Typed.Ports<Pump>(("Inlet", p => p.Inlet), ("Outlet", p => p.Outlet)),
            GetSignalPort = Typed.SignalPorts<Pump>(("Signal", p => p.SignalPort)),
            ReadOutputs = Typed.Outputs<Pump>(pump =>
            {
                string headUnit = pump.Envelope.HeadUnit switch
                {
                    PumpHeadUnit.Metres => "m",
                    PumpHeadUnit.Kilopascals => "kPa",
                    _ => "bar"
                };
                return
                [
                    new OutputValue("Power", pump.Power, "kW"),
                    new OutputValue("Hydraulic power", pump.HydraulicPower, "kW"),
                    new OutputValue("Volume flow", pump.VolumeFlow, "m³/h"),
                    new OutputValue("Pressure rise", pump.PressureRise, "bar"),
                    new OutputValue("Head", pump.Head, headUnit),
                    new OutputValue("Envelope position", pump.Position, "-"),
                    new OutputValue("Max. head at this flow", pump.MaxHeadAtFlow, headUnit),
                    new OutputValue("Min. head at this flow", pump.MinHeadAtFlow, headUnit),
                    new OutputValue("Setpoint error", pump.SetpointError, "bar"),
                    .. ControlOutputs(pump.Control.SignalControl, pump.ControlError)
                ];
            })
        };
    }

    public static ComponentDescriptor ExpansionValve()
    {
        static bool Controlled(NodeModel node) => ChoiceIs(node, "mode", "signal");

        return new ComponentDescriptor
        {
            TypeId = nameof(CycleCalculator.CycleModel.Model.ExpansionValve),
            DisplayName = "Expansion valve",
            Category = Categories.Valves,
            NamePrefix = "EXV",
            Description = "Isenthalpic expansion. The outlet pressure is fixed, controlled to any value measured " +
                          "through the signal input (e.g. a superheat, temperature or mass flow), or left free.",
            Ports = [.. Inline(), SignalInput(Controlled, Measurements.Superheat.Key)],
            Parameters =
            [
                new ParameterDescriptor("mode", "Control mode", ParameterKind.Choice)
                {
                    DefaultChoice = "fixed",
                    Options =
                    [
                        new ChoiceOption("fixed", "Fixed outlet pressure"),
                        new ChoiceOption("signal", "Setpoint (signal input)"),
                        new ChoiceOption("free", "Free (set by other components)")
                    ]
                },
                new ParameterDescriptor("outletPressure", "Outlet pressure", ParameterKind.Number)
                {
                    Unit = "bar", DefaultNumber = 35, Min = 0, MinExclusive = true,
                    IsActive = node => ChoiceIs(node, "mode", "fixed")
                },
                .. SignalControlParameters(Controlled, Measurements.Superheat.Key),
                new ParameterDescriptor("minOutletPressure", "Min. outlet pressure", ParameterKind.Number)
                {
                    Unit = "bar", Optional = true, Min = 0, MinExclusive = true, IsActive = Controlled, Nested = true
                },
                new ParameterDescriptor("maxOutletPressure", "Max. outlet pressure", ParameterKind.Number)
                {
                    Unit = "bar", Optional = true, Min = 0, MinExclusive = true, IsActive = Controlled, Nested = true
                },
                ControlSignParameter(Controlled, "A higher outlet pressure",
                    "E.g. a mass flow measured through the valve falls with a higher outlet pressure.")
            ],
            Create = c => new ExpansionValve(c.Name, c.Fluid(), c.Backend(),
                outletPressure: c.OptionalNumber("outletPressure"),
                control: Controlled(c.Node)
                    ? Control(c, c.OptionalNumber("minOutletPressure"), c.OptionalNumber("maxOutletPressure"))
                    : null),
            GetPort = Typed.Ports<ExpansionValve>(("Inlet", v => v.Inlet), ("Outlet", v => v.Outlet)),
            GetSignalPort = Typed.SignalPorts<ExpansionValve>(("Signal", v => v.SignalPort)),
            ReadOutputs = Typed.Outputs<ExpansionValve>(v =>
            [
                new OutputValue("Outlet pressure", v.OutletPressure, "bar"),
                .. ControlOutputs(v.Control, v.ControlError)
            ])
        };
    }

    public static ComponentDescriptor Pipe() => new()
    {
        TypeId = nameof(CycleCalculator.CycleModel.Model.Pipe),
        DisplayName = "Pipe",
        Category = Categories.Flow,
        NamePrefix = "Pipe",
        Description = "Adiabatic pipe or flow resistance. The pressure drop scales with the mass flow: " +
                      "Δp = Δp_nominal · (ṁ / ṁ_nominal)^n.",
        Ports = Inline(),
        Parameters =
        [
            new ParameterDescriptor("nominalPressureDrop", "Nominal pressure drop", ParameterKind.Number)
            {
                Unit = "bar", DefaultNumber = 0.5, Min = 0,
                Help = "Pressure drop at the nominal mass flow. Zero gives an ideal pipe."
            },
            new ParameterDescriptor("nominalMassFlow", "Nominal mass flow", ParameterKind.Number)
            {
                Unit = "kg/s", DefaultNumber = 1, Min = 0, MinExclusive = true
            },
            new ParameterDescriptor("exponent", "Flow exponent", ParameterKind.Number)
            {
                Unit = "-", DefaultNumber = 2, Min = 1,
                Help = "2 for fully turbulent flow, about 1.75 for smooth pipes, 1 for laminar flow."
            }
        ],
        Create = c => new Pipe(c.Name, c.Fluid(), c.Backend(),
            c.Number("nominalPressureDrop"), c.Number("nominalMassFlow"), c.Number("exponent")),
        GetPort = Typed.Ports<Pipe>(("Inlet", p => p.Inlet), ("Outlet", p => p.Outlet)),
        ReadOutputs = Typed.Outputs<Pipe>(p => [new OutputValue("Pressure drop", p.PressureDrop, "bar")])
    };

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

    /// <summary>Choice of a signal operator's operation; values are <see cref="SignalOperation"/> names.</summary>
    public const string OperationKey = "operation";

    /// <summary>The operation a signal operator node has selected.</summary>
    public static SignalOperation OperationOf(NodeModel node) =>
        node.Choices.TryGetValue(OperationKey, out string? value) && Enum.TryParse(value, out SignalOperation operation)
            ? operation
            : SignalOperation.Minimum;

    public static ComponentDescriptor SignalOperator()
    {
        const string Group = "inputs";
        const string InputKey = "In";
        const string InputSignalKey = "inputSignal";
        static bool UsesConstant(NodeModel node) => OperationOf(node).UsesConstant();

        // The measured quantity of input `index`, falling back to superheat.
        static MeasuredQuantity InputQuantity(NodeModel node, int index) =>
            (node.Choices.TryGetValue(RepeatGroupDescriptor.ItemKey(InputSignalKey, index), out string? key)
                ? Measurements.Find(key)
                : null) ?? Measurements.Superheat;

        static ChoiceOption Option(MeasuredQuantity quantity) => new(quantity.Key, quantity.Label);

        static string Unit(SignalOperator op) =>
            op.Operation is SignalOperation.Multiply or SignalOperation.Divide ||
            op.Measurements.Select(m => Measurements.For(m)?.Unit).Distinct().ToArray() is not [{ } unit]
                ? ""
                : unit;

        return new ComponentDescriptor
        {
            TypeId = nameof(CycleCalculator.CycleModel.Model.SignalOperator),
            DisplayName = "Signal operator",
            Category = Categories.Signals,
            NamePrefix = "Op",
            Description = "Combines measured values into one signal that controllers can follow, e.g. the minimum " +
                          "superheat of several evaporators. Minimum, maximum and mean combine two or more inputs; " +
                          "add, subtract, multiply and divide apply a constant to one input. Connect a controller's " +
                          "signal input to the output and let it measure the operator output.",
            Streams = [],
            Ports =
            [
                new PortDescriptor(InputKey, "Input", PortKind.SignalInput, PortDirection.Undefined, 0, 0, 0.5)
                {
                    Group = Group,
                    IsRequired = _ => true,
                    CheckSource = (node, inputKey, source) =>
                        InputQuantity(node, RepeatGroupDescriptor.Parse(inputKey).Index).Problem(source)
                },
                new PortDescriptor("Out", "Output", PortKind.SignalOutput, PortDirection.Undefined, 0, 1, 0.5)
            ],
            RepeatGroups =
            [
                new RepeatGroupDescriptor(Group, "Input")
                {
                    MinCount = 2, MaxCount = 6,
                    FixedCount = node => UsesConstant(node) ? 1 : null,
                    Help = "Each input is a signal port on the left of the symbol. Connect it to the fluid port to " +
                           "measure, or to another operator's output."
                }
            ],
            Parameters =
            [
                new ParameterDescriptor(OperationKey, "Operation", ParameterKind.Choice)
                {
                    DefaultChoice = nameof(SignalOperation.Minimum),
                    Options =
                    [
                        new ChoiceOption(nameof(SignalOperation.Minimum), "Minimum"),
                        new ChoiceOption(nameof(SignalOperation.Maximum), "Maximum"),
                        new ChoiceOption(nameof(SignalOperation.Mean), "Mean"),
                        new ChoiceOption(nameof(SignalOperation.Add), "Add a constant"),
                        new ChoiceOption(nameof(SignalOperation.Subtract), "Subtract a constant"),
                        new ChoiceOption(nameof(SignalOperation.Multiply), "Multiply by a constant"),
                        new ChoiceOption(nameof(SignalOperation.Divide), "Divide by a constant")
                    ]
                },
                new ParameterDescriptor("constant", "Constant", ParameterKind.Number)
                {
                    DefaultNumber = 1, IsActive = UsesConstant,
                    Help = "Applied to the single input, in the input's unit for add and subtract."
                },
                new ParameterDescriptor(InputSignalKey, "Measured quantity", ParameterKind.Choice)
                {
                    Group = Group,
                    DefaultChoice = Measurements.Superheat.Key,
                    Options = Measurements.All.Select(Option).ToArray(),
                    OptionsFor = (node, key, document, catalog) =>
                    {
                        int index = RepeatGroupDescriptor.Parse(key).Index;
                        return Measurements.AvailableFor(node, RepeatGroupDescriptor.ItemKey(InputKey, index),
                            InputQuantity(node, index).Key, document, catalog).Select(Option).ToArray();
                    },
                    Help = "What this input measures at the connected port, or the output of another operator."
                }
            ],
            Create = c => new SignalOperator(c.Name, OperationOf(c.Node),
                Enumerable.Range(0, c.Count(Group))
                    .Select(index => (Measurements.Find(c.Choice(RepeatGroupDescriptor.ItemKey(InputSignalKey, index)))
                                      ?? Measurements.Superheat).ToBackend())
                    .ToList(),
                UsesConstant(c.Node) ? c.Number("constant") : 0),
            GetPort = (component, key) =>
                throw new KeyNotFoundException($"The signal operator '{component.Name}' has no fluid port '{key}'."),
            GetSignalPort = (component, key) =>
                RepeatGroupDescriptor.Parse(key) is (InputKey, var index) &&
                index < ((SignalOperator)component).Inputs.Count
                    ? ((SignalOperator)component).Inputs[index]
                    : throw new KeyNotFoundException($"The signal operator '{component.Name}' has no input '{key}'."),
            GetSignalOutput = (component, key) => key == "Out"
                ? ((SignalOperator)component).Output
                : throw new KeyNotFoundException($"The signal operator '{component.Name}' has no output '{key}'."),
            ReadOutputs = Typed.Outputs<SignalOperator>(op =>
            [
                new OutputValue("Output", op.Value, Unit(op)),
                .. op.Measurements.Select((measurement, i) => new OutputValue($"Input {i + 1}",
                    op.InputValues is { } values ? values[i] : null, Measurements.For(measurement)?.Unit ?? ""))
            ])
        };
    }

    public static ComponentDescriptor En12900Compressor(CompressorLibrary library)
    {
        static bool DischargeIsPressure(NodeModel node) => ChoiceIs(node, "dischargeCondition", "pressure");
        static bool Controlled(NodeModel node) => ChoiceIs(node, "capacityControl", "signal");
        static bool VariableSpeed(NodeModel node, int index) =>
            ChoiceIs(node, RepeatGroupDescriptor.ItemKey(SpeedControlKey, index), "variable");
        const string Group = "compressors";

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
                          "corrected from the map's nominal superheat to the actual suction density. Add more " +
                          "compressors to model several running in parallel; their flows and powers add up. With " +
                          "speed control, the variable-speed compressors change frequency to " +
                          "hold any value measured through the signal input, e.g. a pressure, a heat transfer or a " +
                          "pump's envelope position.",
            Ports = CompressorPorts(Controlled),
            RepeatGroups =
            [
                new RepeatGroupDescriptor(Group, "Compressor")
                {
                    Help = "Compressors in parallel share the suction and discharge conditions, units and nominal superheat. " +
                           "Only variable-speed compressors can run away from their nominal frequency."
                }
            ],
            Check = node => En12900Library.Check(node, library),
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
                new ParameterDescriptor("capacityControl", "Capacity control", ParameterKind.Choice)
                {
                    DefaultChoice = "none",
                    Options =
                    [
                        new ChoiceOption("none", "None (operating frequencies)"),
                        new ChoiceOption("signal", "Speed control (signal input)")
                    ]
                },
                .. SignalControlParameters(Controlled, Measurements.Pressure.Key),
                new ParameterDescriptor("minFrequency", "Min. frequency", ParameterKind.Number)
                {
                    Unit = "Hz", DefaultNumber = 25, Min = 0, MinExclusive = true, IsActive = Controlled, Nested = true
                },
                new ParameterDescriptor("maxFrequency", "Max. frequency", ParameterKind.Number)
                {
                    Unit = "Hz", DefaultNumber = 70, Min = 0, MinExclusive = true, IsActive = Controlled, Nested = true
                },
                ControlSignParameter(Controlled, "More capacity",
                    "E.g. a suction-side pressure falls with more capacity, a discharge-side pressure rises."),
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
                En12900Library.ModelParameter(library, Group),
                new ParameterDescriptor("nominalFrequency", "Nominal frequency", ParameterKind.Number)
                {
                    Group = Group, Unit = "Hz", DefaultNumber = En12900Polynomial.DefaultNominalFrequency,
                    Min = 0, MinExclusive = true,
                    Help = "Compressor frequency at which the polynomials were measured."
                },
                new ParameterDescriptor(SpeedControlKey, "Speed", ParameterKind.Choice)
                {
                    Group = Group, DefaultChoice = "fixed",
                    Options =
                    [
                        new ChoiceOption("fixed", "Fixed speed (nominal frequency)"),
                        new ChoiceOption("variable", "Variable speed (frequency controlled)")
                    ],
                    Help = "A fixed-speed compressor always runs at its nominal frequency. A variable-speed one can be " +
                           "given an operating frequency, or follows the speed controller when that is on."
                },
                new ParameterDescriptor("operatingFrequency", "Operating frequency", ParameterKind.Number)
                {
                    Group = Group, Unit = "Hz", Optional = true, Min = 0, MinExclusive = true,
                    IsActive = node => !Controlled(node), IsActiveForItem = VariableSpeed,
                    Help = "Optional. Mass flow, power and current scale linearly with operating / nominal " +
                           "frequency. Leave empty to run at the nominal frequency."
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
                    Group = Group, Length = 10, CoefficientLabels = terms, Help = polynomialHelp,
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
                    Group = Group, Length = 10, CoefficientLabels = terms, Help = polynomialHelp,
                    DefaultCoefficients =
                    [
                        -15179.3465346534, -464.76089108911, 754.703465346531, -4.01655742574256, 1.45824059405942,
                        -3.04439702970294, 0.0102332673267327, -0.0566240792079209, 0.0384600297029703, 0.00535540990099
                    ]
                },
                new ParameterDescriptor("currentPolynomial", "Current polynomial (A)", ParameterKind.Coefficients)
                {
                    Group = Group, Length = 10, CoefficientLabels = terms, Optional = true,
                    Help = "Optional. Leave empty if the current is not needed; with several compressors, give it for all or none."
                }
            ],
            Create = c =>
            {
                double superheat = c.Number("nominalSuperheat");
                En12900CompressorMap[] maps = Enumerable.Range(0, c.Count(Group)).Select(index =>
                {
                    double[]? current = c.OptionalCoefficients(RepeatGroupDescriptor.ItemKey("currentPolynomial", index));
                    double nominalFrequency = c.Number(RepeatGroupDescriptor.ItemKey("nominalFrequency", index));
                    return new En12900CompressorMap(
                        new En12900Polynomial(c.Coefficients(RepeatGroupDescriptor.ItemKey("massFlowPolynomial", index)), superheat, nominalFrequency),
                        new En12900Polynomial(c.Coefficients(RepeatGroupDescriptor.ItemKey("powerPolynomial", index)), superheat, nominalFrequency),
                        current is null ? null : new En12900Polynomial(current, superheat, nominalFrequency),
                        c.OptionalNumber(RepeatGroupDescriptor.ItemKey("operatingFrequency", index)),
                        VariableSpeed(c.Node, index));
                }).ToArray();
                return new En12900Compressor(c.Name, c.Fluid(), c.Backend(), maps,
                    Enum.Parse<En12900MassFlowUnit>(c.Choice("massFlowUnit")),
                    Enum.Parse<En12900PowerUnit>(c.Choice("powerUnit")),
                    Enum.Parse<En12900DischargeVariable>(c.Choice("dischargeVariable")),
                    c.Choice("dischargeCondition") == "pressure"
                        ? CompressorDischargeCondition.AtPressure(c.Number("dischargePressure"))
                        : CompressorDischargeCondition.AtSaturationTemperature(c.Number("dischargeSaturationTemperature")),
                    Controlled(c.Node)
                        ? Control(c, c.Number("minFrequency"), c.Number("maxFrequency"))
                        : null);
            },
            EnergyKind = EnergyKind.Power,
            GetPort = Typed.Ports<En12900Compressor>(("Inlet", s => s.Inlet), ("Outlet", s => s.Outlet)),
            GetSignalPort = Typed.SignalPorts<En12900Compressor>(("Signal", s => s.SignalPort)),
            ReadOutputs = Typed.Outputs<En12900Compressor>(compressor =>
            {
                En12900CompressorPerformance performance =
                    compressor.CalculatePerformance(compressor.Inlet.Pressure, compressor.Inlet.Enthalpy);
                List<OutputValue> outputs =
                [
                    new("Power", performance.PowerKilowatts, "kW"),
                    new("Suction sat. temperature", performance.SuctionSaturationTemperature, "°C"),
                    new("Discharge sat. temperature", performance.DischargeSaturationTemperature, "°C"),
                    new("Current", performance.CurrentAmperes, "A"),
                    new("Controlled frequency", compressor.ControlledFrequency, "Hz"),
                    .. ControlOutputs(compressor.CapacityControl, compressor.ControlError)
                ];
                if (performance.Maps.Count > 1)
                {
                    for (int i = 0; i < performance.Maps.Count; i++)
                    {
                        En12900MapPerformance map = performance.Maps[i];
                        outputs.Add(new OutputValue($"Compressor {i + 1} mass flow", map.MassFlowKilogramsPerSecond, "kg/s"));
                        outputs.Add(new OutputValue($"Compressor {i + 1} power", map.PowerKilowatts, "kW"));
                        outputs.Add(new OutputValue($"Compressor {i + 1} current", map.CurrentAmperes, "A"));
                    }
                }

                return outputs;
            })
        };
    }

    public static ComponentDescriptor ThreeWayValve()
    {
        static bool Controlled(NodeModel node) => ChoiceIs(node, "mode", "signal");

        return new ComponentDescriptor
        {
            TypeId = nameof(CycleCalculator.CycleModel.Model.ThreeWayValve),
            DisplayName = "Three-way valve",
            Category = Categories.Valves,
            NamePrefix = "3WV",
            Description = "Splits the flow between two outlets, either with a fixed opening or adjusted to " +
                          "reach any value measured through the signal input, e.g. a temperature or mass flow.",
            Ports =
            [
                new PortDescriptor("Inlet", "Inlet", PortKind.Fluid, PortDirection.Inlet, 0, 0, 0.5),
                new PortDescriptor("Outlet", "Outlet 1", PortKind.Fluid, PortDirection.Outlet, 0, 1, 0.5),
                new PortDescriptor("Outlet2", "Outlet 2", PortKind.Fluid, PortDirection.Outlet, 0, 0.5, 1),
                SignalInput(Controlled, Measurements.Temperature.Key)
            ],
            Parameters =
            [
                new ParameterDescriptor("mode", "Control mode", ParameterKind.Choice)
                {
                    DefaultChoice = "fixed",
                    Options =
                    [
                        new ChoiceOption("fixed", "Fixed opening"),
                        new ChoiceOption("signal", "Setpoint (signal input)")
                    ]
                },
                new ParameterDescriptor("fixedOpening", "Opening to outlet 1", ParameterKind.Number)
                {
                    Unit = "-", DefaultNumber = 0.5, Min = 0, Max = 1, IsActive = node => !Controlled(node),
                    Help = "Fraction of the inlet flow sent to outlet 1."
                },
                .. SignalControlParameters(Controlled, Measurements.Temperature.Key,
                    (Measurements.Temperature, 30), (Measurements.MassFlow, 0.5)),
                new ParameterDescriptor("minimumOpening", "Minimum opening", ParameterKind.Number)
                {
                    Unit = "-", Optional = true, Min = 0, Max = 0.5, IsActive = Controlled, Nested = true,
                    Help = "Keeps both outlets at least this open."
                },
                ControlSignParameter(Controlled, "More opening to outlet 1",
                    "Depends on where the measured port is.")
            ],
            Create = c =>
            {
                if (!Controlled(c.Node))
                    return new ThreeWayValve(c.Name, c.Fluid(), c.Backend(), fixedOpening: c.Number("fixedOpening"));
                double? minimum = c.OptionalNumber("minimumOpening");
                return new ThreeWayValve(c.Name, c.Fluid(), c.Backend(), Control(c, minimum, 1 - minimum));
            },
            GetPort = Typed.Ports<ThreeWayValve>(("Inlet", v => v.Inlet), ("Outlet", v => v.Outlet), ("Outlet2", v => v.Outlet2)),
            GetSignalPort = Typed.SignalPorts<ThreeWayValve>(("Signal", v => v.SignalPort)),
            ReadOutputs = Typed.Outputs<ThreeWayValve>(v =>
            [
                new OutputValue("Opening to outlet 1", v.Opening, "-"),
                .. ControlOutputs(v.Control, v.ControlError)
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
        static bool Controlled(NodeModel node) => ChoiceIs(node, "mode", "signal");

        return new ComponentDescriptor
        {
            TypeId = nameof(CycleCalculator.CycleModel.Model.FixedEfficiencyHeatExchanger),
            DisplayName = "Heat exchanger (effectiveness)",
            Category = Categories.HeatExchangers,
            NamePrefix = "HX",
            Description = "Counter-current heat exchanger without pressure drop. The effectiveness is fixed, " +
                          "or adjusted so a value measured through the signal input (e.g. a temperature) reaches " +
                          "a setpoint.",
            Streams = ["Stream 1 (top)", "Stream 2 (bottom)"],
            Ports =
            [
                .. CounterCurrent(),
                SignalInput(Controlled, Measurements.Temperature.Key)
            ],
            Parameters =
            [
                new ParameterDescriptor("mode", "Mode", ParameterKind.Choice)
                {
                    DefaultChoice = "fixed",
                    Options = [new ChoiceOption("fixed", "Fixed effectiveness"), new ChoiceOption("signal", "Setpoint (signal input)")]
                },
                new ParameterDescriptor("efficiency", "Effectiveness", ParameterKind.Number)
                {
                    Unit = "-", DefaultNumber = 0.5, Min = 0, Max = 1, IsActive = node => !Controlled(node)
                },
                .. SignalControlParameters(Controlled, Measurements.Temperature.Key, (Measurements.Temperature, 120)),
                new ParameterDescriptor("minEfficiency", "Min. effectiveness", ParameterKind.Number)
                {
                    Unit = "-", Optional = true, Min = 0, Max = 1, IsActive = Controlled, Nested = true
                },
                new ParameterDescriptor("maxEfficiency", "Max. effectiveness", ParameterKind.Number)
                {
                    Unit = "-", Optional = true, Min = 0, Max = 1, IsActive = Controlled, Nested = true
                },
                ControlSignParameter(Controlled, "A higher effectiveness",
                    "E.g. the hot stream's outlet temperature falls with a higher effectiveness."),
                .. ProfileModelParameters()
            ],
            Create = c => new FixedEfficiencyHeatExchanger(c.Name, c.Fluid(0), c.Backend(0), c.Fluid(1), c.Backend(1),
                efficiency: c.OptionalNumber("efficiency"),
                control: Controlled(c.Node)
                    ? Control(c, c.OptionalNumber("minEfficiency"), c.OptionalNumber("maxEfficiency"))
                    : null,
                model: ProfileModel(c),
                singlePhaseSegments: SinglePhaseSegments(c)),
            EnergyKind = EnergyKind.Exchanger,
            GetPort = Typed.Ports<FixedEfficiencyHeatExchanger>(("Inlet", h => h.Inlet), ("Outlet", h => h.Outlet),
                ("Inlet2", h => h.Inlet2), ("Outlet2", h => h.Outlet2)),
            GetSignalPort = Typed.SignalPorts<FixedEfficiencyHeatExchanger>(("Signal", h => h.SignalPort)),
            ReadOutputs = Typed.Outputs<FixedEfficiencyHeatExchanger>(h =>
            [
                new OutputValue("Effectiveness", h.Efficiency, "-"),
                new OutputValue("Heat flow", Math.Abs(h.Inlet.MassFlow * (h.Outlet.Enthalpy - h.Inlet.Enthalpy)), "kW"),
                .. ControlOutputs(h.Control, h.ControlError)
            ]),
            ReadTemperatureProfile = Typed.Profile<FixedEfficiencyHeatExchanger>(h => h.GetTemperatureProfile())
        };
    }

    private static bool UsesMovingBoundary(NodeModel node) => ChoiceIs(node, "profileModel", "movingBoundary");

    /// <summary>Choice of temperature profile model shared by the counter-current heat exchangers.</summary>
    private static ParameterDescriptor[] ProfileModelParameters() =>
    [
        new ParameterDescriptor("profileModel", "Profile model", ParameterKind.Choice)
        {
            DefaultChoice = "movingBoundary",
            Options = [new ChoiceOption("movingBoundary", "Moving boundary"), new ChoiceOption("discretized", "Discretized")],
            Help = "Discretized evaluates both fluids at fixed points along the exchanger for every trial heat flow. " +
                   "Moving boundary splits each stream into zones at its phase changes and evaluates the fluids only " +
                   "to build those zones, which needs far fewer property calls, especially with the HEOS backend."
        },
        new ParameterDescriptor("singlePhaseSegments", "Single-phase segments", ParameterKind.Number)
        {
            Unit = "-", DefaultNumber = MovingBoundaryProfile.DefaultSinglePhaseSegments,
            Min = 1, Max = MovingBoundaryProfile.MaximumSinglePhaseSegments, IsActive = UsesMovingBoundary,
            Help = "Number of segments each single-phase zone is split into, to follow a changing heat capacity. " +
                   "More segments are more accurate (most noticeable for supercritical CO2) but slower."
        }
    ];

    private static HeatExchangerModel ProfileModel(BuildContext c) =>
        UsesMovingBoundary(c.Node) ? HeatExchangerModel.MovingBoundary : HeatExchangerModel.Discretized;

    private static int SinglePhaseSegments(BuildContext c) =>
        UsesMovingBoundary(c.Node)
            ? (int)Math.Round(c.Number("singlePhaseSegments"))
            : MovingBoundaryProfile.DefaultSinglePhaseSegments;

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
            new ParameterDescriptor("ua", "UA", ParameterKind.Number) { Unit = "kW/K", DefaultNumber = 20, Min = 0 },
            .. ProfileModelParameters()
        ],
        Create = c => new FixedUaHeatExchanger(c.Name, c.Fluid(0), c.Backend(0), c.Fluid(1), c.Backend(1), c.Number("ua"),
            ProfileModel(c), SinglePhaseSegments(c)),
        EnergyKind = EnergyKind.Exchanger,
        GetPort = Typed.Ports<FixedUaHeatExchanger>(("Inlet", h => h.Inlet), ("Outlet", h => h.Outlet),
            ("Inlet2", h => h.Inlet2), ("Outlet2", h => h.Outlet2)),
        ReadOutputs = Typed.Outputs<FixedUaHeatExchanger>(h => [new OutputValue("Heat flow", h.HeatTransferRate, "kW")]),
        ReadTemperatureProfile = Typed.Profile<FixedUaHeatExchanger>(h => h.GetTemperatureProfile())
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
        EnergyKind = EnergyKind.Heat,
        GetPort = Typed.Ports<FixedUaEvaporator>(("Inlet", e => e.Inlet), ("Outlet", e => e.Outlet)),
        ReadOutputs = Typed.Outputs<FixedUaEvaporator>(e =>
        [
            new OutputValue("Evaporating pressure", e.EvaporatingPressure, "bar"),
            new OutputValue("Secondary mass flow", e.SecondaryMassFlow, "kg/s"),
            new OutputValue("Cooling capacity", Math.Abs(e.Inlet.MassFlow * (e.Outlet.Enthalpy - e.Inlet.Enthalpy)), "kW")
        ]),
        ReadTemperatureProfile = Typed.Profile<FixedUaEvaporator>(e => e.GetTemperatureProfile())
    };

    /// <summary>Stream 1 runs left to right along the top, stream 2 right to left along the bottom.</summary>
    private static PortDescriptor[] CounterCurrent() =>
    [
        new("Inlet", "Inlet 1", PortKind.Fluid, PortDirection.Inlet, 0, 0, 0.25),
        new("Outlet", "Outlet 1", PortKind.Fluid, PortDirection.Outlet, 0, 1, 0.25),
        new("Inlet2", "Inlet 2", PortKind.Fluid, PortDirection.Inlet, 1, 1, 0.75),
        new("Outlet2", "Outlet 2", PortKind.Fluid, PortDirection.Outlet, 1, 0, 0.75)
    ];

    /// <summary>Per-compressor choice of an EN 12900 compressor: fixed or variable speed.</summary>
    public const string SpeedControlKey = "speedControl";

    /// <summary>Choice of what a controller's signal input measures; values are <see cref="MeasuredQuantity.Key"/>.</summary>
    public const string ControlSignalKey = "controlSignal";

    /// <summary>The measured quantity a node has selected, falling back to the controller's default.</summary>
    private static MeasuredQuantity SelectedQuantity(NodeModel node, string defaultSignal) =>
        (node.Choices.TryGetValue(ControlSignalKey, out string? key) ? Measurements.Find(key) : null)
        ?? Measurements.Find(defaultSignal)!;

    /// <summary>The signal input of a controller, checking that its source provides the selected quantity.</summary>
    private static PortDescriptor SignalInput(Func<NodeModel, bool> controlled, string defaultSignal) =>
        new("Signal", "Measurement", PortKind.SignalInput, PortDirection.Undefined, 0, 0.5, 0)
        {
            IsRequired = controlled,
            CheckSource = (node, _, source) =>
                controlled(node) ? SelectedQuantity(node, defaultSignal).Problem(source) : null
        };

    /// <summary>Inlet and outlet in line, with a signal input on top for capacity control.</summary>
    private static PortDescriptor[] CompressorPorts(Func<NodeModel, bool> controlled) =>
        [.. Inline(), SignalInput(controlled, Measurements.Pressure.Key)];

    /// <summary>
    /// The measured quantity and one setpoint per quantity, shared by all controllers so the setpoint keys mean the
    /// same everywhere. <paramref name="setpointDefaults"/> overrides the default setpoint of single quantities.
    /// </summary>
    private static ParameterDescriptor[] SignalControlParameters(Func<NodeModel, bool> controlled, string defaultSignal,
        params (MeasuredQuantity Quantity, double Setpoint)[] setpointDefaults)
    {
        ChoiceOption Option(MeasuredQuantity quantity) => new(quantity.Key, quantity.Label);

        return
        [
            new(ControlSignalKey, "Measured quantity", ParameterKind.Choice)
            {
                DefaultChoice = defaultSignal,
                Options = Measurements.All.Select(Option).ToArray(),
                OptionsFor = (node, _, document, catalog) => Measurements
                    .AvailableFor(node, "Signal", SelectedQuantity(node, defaultSignal).Key, document, catalog)
                    .Select(Option).ToArray(),
                IsActive = controlled,
                Nested = true,
                Help = "What the signal input measures: a state of the connected port, a value the connected " +
                       "component provides, e.g. a pump's envelope position, or the output of a signal operator."
            },
            .. Measurements.All.Select(quantity => new ParameterDescriptor(quantity.SetpointKey,
                $"{quantity.Label} setpoint", ParameterKind.Number)
            {
                Unit = quantity.Unit,
                DefaultNumber = setpointDefaults.Where(d => d.Quantity == quantity).Select(d => (double?)d.Setpoint)
                    .FirstOrDefault() ?? quantity.DefaultSetpoint,
                Min = quantity.Min, Max = quantity.Max, MinExclusive = quantity.MinExclusive,
                IsActive = node => controlled(node) && SelectedQuantity(node, defaultSignal) == quantity,
                Nested = true,
                Help = quantity.Help + " If it cannot be reached, the controlled variable stays at its limit."
            })
        ];
    }

    /// <param name="increase">What raising the controlled variable means, e.g. "More capacity".</param>
    private static ParameterDescriptor ControlSignParameter(Func<NodeModel, bool> controlled, string increase,
        string example) =>
        new("controlSign", "Control direction", ParameterKind.Choice)
        {
            DefaultChoice = "auto",
            Options =
            [
                new ChoiceOption("auto", "Automatic"),
                new ChoiceOption("positive", $"{increase} raises the measured value (+1)"),
                new ChoiceOption("negative", $"{increase} lowers the measured value (−1)")
            ],
            IsActive = controlled,
            Nested = true,
            Help = example + " Automatic lets the solver determine it from the initial guess."
        };

    /// <summary>The setpoint control of a node, with the bounds of its controlled variable.</summary>
    private static SignalControl Control(BuildContext c, double? minimum, double? maximum)
    {
        MeasuredQuantity quantity = Measurements.Find(c.Choice(ControlSignalKey)) ?? Measurements.Pressure;
        double setpoint = c.Number(quantity.SetpointKey);
        int? sign = c.Choice("controlSign") switch { "positive" => 1, "negative" => -1, _ => null };
        try
        {
            return new SignalControl(quantity.ToBackend(), setpoint, minimum, maximum, sign);
        }
        catch (ArgumentOutOfRangeException ex) when (ex.ParamName == "setpoint")
        {
            throw new ParameterException(quantity.SetpointKey, CleanMessage(ex));
        }
    }

    /// <summary>The measured value and control error after a solve, in the unit of the measured quantity.</summary>
    private static OutputValue[] ControlOutputs(SignalControl? control, double? error)
    {
        MeasuredQuantity? quantity = Measurements.For(control?.Measurement);
        return
        [
            new OutputValue("Measured value", control is not null && error is { } e ? control.Setpoint + e : null,
                quantity?.Unit ?? ""),
            new OutputValue("Control error", error, quantity?.DifferenceUnit ?? "")
        ];
    }

    /// <summary>Removes the " (Parameter 'x')" suffix .NET adds to argument exception messages.</summary>
    private static string CleanMessage(ArgumentException ex) =>
        ex.Message.IndexOf(" (Parameter '", StringComparison.Ordinal) is var index and >= 0 ? ex.Message[..index] : ex.Message;

    /// <summary>Inlet on the left, outlet on the right.</summary>
    private static PortDescriptor[] Inline(int stream = 0) =>
    [
        new("Inlet", "Inlet", PortKind.Fluid, PortDirection.Inlet, stream, 0, 0.5),
        new("Outlet", "Outlet", PortKind.Fluid, PortDirection.Outlet, stream, 1, 0.5)
    ];
}
