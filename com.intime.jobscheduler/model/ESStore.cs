﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace com.intime.jobscheduler.Job
{
    class ESStore
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public string Location { get; set; }
        public string Tel { get; set; }
        public decimal? Longitude { get; set; }
        public decimal? Latitude { get; set; }        
        public Nullable<decimal> GpsLat { get; set; }
        public Nullable<decimal> GpsLng { get; set; }
        public Nullable<decimal> GpsAlt { get; set; }

    }
}