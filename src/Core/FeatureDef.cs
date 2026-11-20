using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorks.Interop.swpublished;
using static MfgChamfer.Core;

namespace MfgChamfer
{
    // What the add-in host needs to know about a feature: its command and the hooks around it.
    // One instance per feature, listed in Addin.Features (see docs\adding-new-feature.md).
    public abstract class FeatureDef
    {
        public const int CmdCopy = 583, CmdCut = 584, CmdPaste = 585;   // swCommands_e

        public abstract string Name { get; }        // command / menu title ("CNC Chamfer")
        public abstract string Hint { get; }        // tooltip of the command
        public virtual int IconIndex { get { return 0; } }   // position of the feature's icon in the toolbar_*.png strips
        public abstract void Insert(ISldWorks sw, IModelDoc2 model);   // opens the PropertyManager page for a new feature

        // Copy / Paste hook (SOLIDWORKS cannot copy macro features). Return 1 when the command was handled here, else 0.
        // model is the active part or null. Only called for Copy, Cut and Paste.
        public virtual int OnCommandPre(ISldWorks sw, IModelDoc2 model, int command) { return 0; }
    }
}
