using System;
using System.Collections.Generic;
using System.Text;

namespace MyFlightbook.Core.Aircraft
{
    public class AircraftInstance
    {
        static public IEnumerable<AircraftInstance> GetInstanceTypes()
        {
            const string szCacheKey = "aiCacheKey";

            if (HttpRuntime.Cache != null &&
                HttpRuntime.Cache[szCacheKey] != null)
                return (AircraftInstance[])HttpRuntime.Cache[szCacheKey];

            List<AircraftInstance> al = new List<AircraftInstance>();

            DBHelper dbh = new DBHelper("SELECT * FROM aircraftinstancetypes");
            if (!dbh.ReadRows((comm) => { }, (dr) => { al.Add(new AircraftInstance(dr)); }))
                throw new MyFlightbookException("Error getting instance types:\r\n" + dbh.LastError);

            if (HttpRuntime.Cache != null)
                HttpRuntime.Cache[szCacheKey] = al.ToArray();

            return al.ToArray;
        }
    }
}
