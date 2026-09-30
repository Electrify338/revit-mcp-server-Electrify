using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.Reinforcement;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.Reinforcement
{
    /// <summary>
    /// Command for a rebar tool whose handler reads its own JSON parameters: passes them through,
    /// raises the external event and waits for the result.
    /// </summary>
    public abstract class ReinforcementCommandBase : ExternalEventCommandBase
    {
        private readonly string _title;

        protected ReinforcementCommandBase(ReinforcementEventHandlerBase handler, UIApplication uiApp, string title)
            : base(handler, uiApp)
        {
            _title = title;
        }

        protected virtual int TimeoutMilliseconds => 60000;

        public override object Execute(JObject parameters, string requestId)
        {
            try
            {
                var handler = (ReinforcementEventHandlerBase)Handler;
                handler.SetParameters(parameters);

                if (RaiseAndWaitForCompletion(TimeoutMilliseconds))
                    return handler.Result;
                throw new TimeoutException($"{_title} timed out");
            }
            catch (Exception ex)
            {
                throw new Exception($"{_title} failed: {ex.Message}");
            }
        }
    }

    public class GetRebarQuantitiesCommand : ReinforcementCommandBase
    {
        public override string CommandName => "get_rebar_quantities";

        public GetRebarQuantitiesCommand(UIApplication uiApp)
            : base(new GetRebarQuantitiesEventHandler(), uiApp, "Get rebar quantities")
        {
        }
    }

    public class GetViewRebarCommand : ReinforcementCommandBase
    {
        public override string CommandName => "get_view_rebar";

        public GetViewRebarCommand(UIApplication uiApp)
            : base(new GetViewRebarEventHandler(), uiApp, "Get view rebar")
        {
        }
    }

    public class CreateRebarFromShapeCommand : ReinforcementCommandBase
    {
        public override string CommandName => "create_rebar_from_shape";

        public CreateRebarFromShapeCommand(UIApplication uiApp)
            : base(new CreateRebarFromShapeEventHandler(), uiApp, "Create rebar from shape")
        {
        }
    }

    public class PropagateRebarCommand : ReinforcementCommandBase
    {
        public override string CommandName => "propagate_rebar";

        protected override int TimeoutMilliseconds => 150000;

        public PropagateRebarCommand(UIApplication uiApp)
            : base(new PropagateRebarEventHandler(), uiApp, "Propagate rebar")
        {
        }
    }

    public class SetRebarLayoutCommand : ReinforcementCommandBase
    {
        public override string CommandName => "set_rebar_layout";

        public SetRebarLayoutCommand(UIApplication uiApp)
            : base(new SetRebarLayoutEventHandler(), uiApp, "Set rebar layout")
        {
        }
    }

    public class SetRebarTerminationsCommand : ReinforcementCommandBase
    {
        public override string CommandName => "set_rebar_terminations";

        public SetRebarTerminationsCommand(UIApplication uiApp)
            : base(new SetRebarTerminationsEventHandler(), uiApp, "Set rebar terminations")
        {
        }
    }

    public class SetRebarCoverCommand : ReinforcementCommandBase
    {
        public override string CommandName => "set_rebar_cover";

        public SetRebarCoverCommand(UIApplication uiApp)
            : base(new SetRebarCoverEventHandler(), uiApp, "Set rebar cover")
        {
        }
    }

    public class SpliceRebarCommand : ReinforcementCommandBase
    {
        public override string CommandName => "splice_rebar";

        protected override int TimeoutMilliseconds => 150000;

        public SpliceRebarCommand(UIApplication uiApp)
            : base(new SpliceRebarEventHandler(), uiApp, "Splice rebar")
        {
        }
    }

    public class SplitRebarSetCommand : ReinforcementCommandBase
    {
        public override string CommandName => "split_rebar_set";

        public SplitRebarSetCommand(UIApplication uiApp)
            : base(new SplitRebarSetEventHandler(), uiApp, "Split rebar set")
        {
        }
    }

    public class CreateAreaReinforcementCommand : ReinforcementCommandBase
    {
        public override string CommandName => "create_area_reinforcement";

        public CreateAreaReinforcementCommand(UIApplication uiApp)
            : base(new CreateAreaReinforcementEventHandler(), uiApp, "Create area reinforcement")
        {
        }
    }

    public class CreatePathReinforcementCommand : ReinforcementCommandBase
    {
        public override string CommandName => "create_path_reinforcement";

        public CreatePathReinforcementCommand(UIApplication uiApp)
            : base(new CreatePathReinforcementEventHandler(), uiApp, "Create path reinforcement")
        {
        }
    }

    public class SetRebarPresentationCommand : ReinforcementCommandBase
    {
        public override string CommandName => "set_rebar_presentation";

        public SetRebarPresentationCommand(UIApplication uiApp)
            : base(new SetRebarPresentationEventHandler(), uiApp, "Set rebar presentation")
        {
        }
    }

    public class TagRebarCommand : ReinforcementCommandBase
    {
        public override string CommandName => "tag_rebar";

        protected override int TimeoutMilliseconds => 150000;

        public TagRebarCommand(UIApplication uiApp)
            : base(new TagRebarEventHandler(), uiApp, "Tag rebar")
        {
        }
    }

    public class CreateBendingDetailCommand : ReinforcementCommandBase
    {
        public override string CommandName => "create_bending_detail";

        protected override int TimeoutMilliseconds => 150000;

        public CreateBendingDetailCommand(UIApplication uiApp)
            : base(new CreateBendingDetailEventHandler(), uiApp, "Create bending detail")
        {
        }
    }

    public class CreateMultiRebarAnnotationCommand : ReinforcementCommandBase
    {
        public override string CommandName => "create_multi_rebar_annotation";

        public CreateMultiRebarAnnotationCommand(UIApplication uiApp)
            : base(new CreateMultiRebarAnnotationEventHandler(), uiApp, "Create multi-rebar annotation")
        {
        }
    }

    public class ManageRebarNumberingCommand : ReinforcementCommandBase
    {
        public override string CommandName => "manage_rebar_numbering";

        public ManageRebarNumberingCommand(UIApplication uiApp)
            : base(new ManageRebarNumberingEventHandler(), uiApp, "Manage rebar numbering")
        {
        }
    }
}
