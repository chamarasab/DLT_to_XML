using System;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using System.Globalization;

public static class DltConverter
{
    public static bool Convert(string inputPath, string outputPath, string xsdPath = null)
    {
        if (!File.Exists(inputPath)) throw new FileNotFoundException("Input DLT not found", inputPath);

        var lines = File.ReadAllLines(inputPath).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
        XNamespace ns = "http://creditinfo.com/schemas/CB5/SriLanka/bouncedcheque";
        var doc = new XDocument(new XDeclaration("1.0", "utf-8", "yes"));
        var batch = new XElement(ns + "Batch");

        var hdhd = lines.Select(l => l.Split('|')).FirstOrDefault(p => p.Length > 1 && p[0] == "HDHD");
        var batchId = hdhd != null && hdhd.Length > 1 ? hdhd[1] : "DLT_BATCH";
        batch.Add(new XElement(ns + "BatchIdentifier", batchId));

        foreach (var parts in lines.Select(l => l.Split('|')).Where(p => p.Length > 0 && p[0] == "CMDC"))
        {
            string Get(int i) => (parts.Length > i && !string.IsNullOrWhiteSpace(parts[i])) ? parts[i].Trim() : null;

            var branchId = Get(2) ?? "";
            var account = Get(3) ?? "";
            var chequeNumber = Get(4) ?? "";
            var amount = Get(5) ?? "";
            var currency = Get(6) ?? "";
            var dateStr = Get(7);
            DateTime? dishonoured = null;
            if (DateTime.TryParseExact(dateStr, new[] { "dd-MMM-yyyy", "d-MMM-yyyy", "dd-MM-yyyy", "yyyy-MM-dd" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                dishonoured = dt;

            var reasonCode = Get(8);
            var reason = reasonCode == "001" ? "InsufficientFunds" : "Unknown";

            var customerCode = Get(10) ?? (account != null ? account + "-1" : null);
            var fullName = Get(17) ?? Get(18) ?? "";

            string addressLine = Get(19) ?? Get(28) ?? "";
            string postal = null;
            for (int i = 0; i < parts.Length; i++)
            {
                var t = Get(i);
                if (!string.IsNullOrEmpty(t) && t.All(c => char.IsDigit(c)) && t.Length >= 3 && t.Length <= 6)
                {
                    postal = t; break;
                }
            }

            var bounced = new XElement(ns + "BouncedCheque",
                new XElement(ns + "EntityCode", string.Join("-", new[] { account, chequeNumber, branchId }.Where(x => !string.IsNullOrEmpty(x))))
            );

            var data = new XElement(ns + "BouncedChequeData",
                new XElement(ns + "BranchID", branchId),
                new XElement(ns + "ChequeNumber", chequeNumber),
                new XElement(ns + "ChequeAmount",
                    new XElement(ns + "Value", amount),
                    new XElement(ns + "Currency", currency)
                ),
                new XElement(ns + "AccountNumber", account)
            );

            if (dishonoured.HasValue)
                data.Add(new XElement(ns + "DateDishonoured", dishonoured.Value.ToString("yyyy-MM-dd")));
            else if (!string.IsNullOrEmpty(dateStr))
                data.Add(new XElement(ns + "DateDishonoured", dateStr));

            data.Add(new XElement(ns + "ReasonForDishonour", reason));
            bounced.Add(data);

            var individual = new XElement(ns + "Individual");
            individual.Add(new XElement(ns + "CustomerCode", customerCode ?? string.Empty));
            individual.Add(new XElement(ns + "FullName", fullName));
            if (!string.IsNullOrEmpty(Get(15))) individual.Add(new XElement(ns + "Salutation", Get(15)));
            if (!string.IsNullOrEmpty(Get(16))) individual.Add(new XElement(ns + "Profession", Get(16)));
            if (!string.IsNullOrEmpty(Get(17))) individual.Add(new XElement(ns + "SpouseName", Get(17)));
            individual.Add(new XElement(ns + "ClassificationOfIndividual", "Individual"));
            if (!string.IsNullOrEmpty(Get(20))) individual.Add(new XElement(ns + "Gender", Get(20)));

            var dobStr = Get(21);
            if (!string.IsNullOrEmpty(dobStr) && DateTime.TryParse(dobStr, out var dob))
                individual.Add(new XElement(ns + "DateOfBirth", dob.ToString("yyyy-MM-dd")));

            var ms = Get(22);
            var allowedMarital = new[] { "Unmarried", "Married", "Widowed", "Divorced", "Separated", "Single" };
            if (!string.IsNullOrEmpty(ms) && ms.Any(char.IsLetter) && allowedMarital.Any(a => string.Equals(a, ms, StringComparison.OrdinalIgnoreCase)))
                individual.Add(new XElement(ns + "MaritalStatus", ms));
            if (!string.IsNullOrEmpty(Get(23))) individual.Add(new XElement(ns + "FateStatus", Get(23)));
            if (!string.IsNullOrEmpty(Get(24))) individual.Add(new XElement(ns + "Employment", Get(24)));
            individual.Add(new XElement(ns + "Residency", "Yes"));

            var idNums = new XElement(ns + "IdentificationNumbers");
            if (!string.IsNullOrEmpty(Get(10))) idNums.Add(new XElement(ns + "NICNumber", Get(10)));
            if (!string.IsNullOrEmpty(Get(11))) idNums.Add(new XElement(ns + "PassportNumber", Get(11)));
            if (!string.IsNullOrEmpty(Get(12))) idNums.Add(new XElement(ns + "DrivingLicenseNumber", Get(12)));
            if (!string.IsNullOrEmpty(Get(13))) idNums.Add(new XElement(ns + "BusinessRegistrationNumber", Get(13)));
            var brd = Get(14);
            if (!string.IsNullOrEmpty(brd) && DateTime.TryParse(brd, out var brdDt)) idNums.Add(new XElement(ns + "BusinessRegistrationDate", brdDt.ToString("yyyy-MM-dd")));
            individual.Add(idNums);

            var mailing = new XElement(ns + "MailingAddress");
            if (!string.IsNullOrEmpty(Get(26))) mailing.Add(new XElement(ns + "City", Get(26)));
            mailing.Add(new XElement(ns + "PostalCode", postal ?? string.Empty));
            var prov = Get(27);
            var allowedProvinces = new[] { "Western", "Central", "Southern", "Northern", "Eastern", "North Western", "North Central", "Uva", "Sabaragamuwa" };
            if (!string.IsNullOrEmpty(prov) && allowedProvinces.Any(p => string.Equals(p, prov, StringComparison.OrdinalIgnoreCase)))
                mailing.Add(new XElement(ns + "Province", prov));
            if (!string.IsNullOrEmpty(Get(28))) mailing.Add(new XElement(ns + "District", Get(28)));
            mailing.Add(new XElement(ns + "Country", "LK"));
            mailing.Add(new XElement(ns + "AddressLine", addressLine));
            individual.Add(mailing);

            var permanent = new XElement(ns + "PermanentAddress");
            if (!string.IsNullOrEmpty(Get(26))) permanent.Add(new XElement(ns + "City", Get(26)));
            permanent.Add(new XElement(ns + "PostalCode", postal ?? string.Empty));
            if (!string.IsNullOrEmpty(prov) && allowedProvinces.Any(p => string.Equals(p, prov, StringComparison.OrdinalIgnoreCase)))
                permanent.Add(new XElement(ns + "Province", prov));
            if (!string.IsNullOrEmpty(Get(28))) permanent.Add(new XElement(ns + "District", Get(28)));
            permanent.Add(new XElement(ns + "Country", "LK"));
            permanent.Add(new XElement(ns + "AddressLine", addressLine));
            individual.Add(permanent);

            var contacts = new XElement(ns + "Contacts");
            if (!string.IsNullOrEmpty(Get(29))) contacts.Add(new XElement(ns + "PhoneNumber", Get(29)));
            if (!string.IsNullOrEmpty(Get(30))) contacts.Add(new XElement(ns + "PhoneNumber2", Get(30)));
            if (contacts.HasElements) individual.Add(contacts);

            bounced.Add(individual);

            var role = new XElement(ns + "SubjectRole",
                new XElement(ns + "CustomerCode", customerCode ?? string.Empty),
                new XElement(ns + "RoleOfCustomer", "Issuer")
            );
            bounced.Add(role);

            batch.Add(bounced);
        }

        doc.Add(batch);
        doc.Save(outputPath);

        // If XSD provided, validate and return whether validation succeeded
        if (!string.IsNullOrEmpty(xsdPath) && File.Exists(xsdPath))
        {
            var schemas = new XmlSchemaSet();
            schemas.Add(ns.NamespaceName, xsdPath);
            var settings = new XmlReaderSettings { ValidationType = ValidationType.Schema, Schemas = schemas };
            var hadErrors = false;
            settings.ValidationEventHandler += (s, e) => { hadErrors = true; };
            using (var xr = XmlReader.Create(outputPath, settings))
            {
                while (xr.Read()) { }
            }
            return !hadErrors;
        }

        return true;
    }
}
