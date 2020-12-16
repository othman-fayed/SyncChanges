﻿using System.Collections.Generic;

namespace SyncChanges
{
    class TableInfo
    {
        public string Name { get; set; }
        public IList<string> KeyColumns { get; set; }
        public IList<string> OtherColumns { get; set; }
        public IList<ForeignKeyConstraint> ForeignKeyConstraints { get; set; }
        public bool HasIdentityColumn { get; set; } = true;
    }
}