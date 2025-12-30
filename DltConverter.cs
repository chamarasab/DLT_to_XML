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
        XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
        var doc = new XDocument(new XDeclaration("1.0", "utf-8", "yes"));
        var batch = new XElement(ns + "Batch",
            new XAttribute(XNamespace.Xmlns + "xsi", xsi.NamespaceName),
            new XAttribute(xsi + "schemaLocation", ns.NamespaceName)
        );

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

            // format amount as decimal with two fraction digits
            string formattedAmount = amount;
            if (!string.IsNullOrEmpty(amount) && Decimal.TryParse(amount, NumberStyles.Any, CultureInfo.InvariantCulture, out var amtDec))
            {
                formattedAmount = amtDec.ToString("F2", CultureInfo.InvariantCulture);
            }

            var data = new XElement(ns + "BouncedChequeData",
                new XElement(ns + "BranchID", branchId),
                new XElement(ns + "ChequeNumber", chequeNumber),
                new XElement(ns + "ChequeAmount",
                    new XElement(ns + "Value", formattedAmount),
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

            // Determine if this is a company or individual.
            bool LooksLikeCompany()
            {
                if (!string.IsNullOrEmpty(Get(10)))
                {
                    var code = Get(10);
                    // company codes often start with letters like PV, WA, K, etc.
                    if (code.Length >= 2 && char.IsLetter(code[0]) && code.Any(char.IsLetter)) return true;
                }
                if (!string.IsNullOrEmpty(fullName))
                {
                    var up = fullName.ToUpperInvariant();
                    if (up.Contains("PVT") || up.Contains("LTD") || up.Contains("COMPANY") || up.Contains("CO") || up.Contains("PLC") || up.Contains("PVT LTD")) return true;
                }
                return false;
            }

            if (LooksLikeCompany())
            {
                var company = new XElement(ns + "Company");
                var compCode = customerCode;
                if (!string.IsNullOrEmpty(compCode) && !compCode.Contains("-")) compCode = compCode + "-3";
                company.Add(new XElement(ns + "CustomerCode", compCode ?? string.Empty));
                company.Add(new XElement(ns + "CompanyName", fullName));
                company.Add(new XElement(ns + "LegalConstitution", "Other"));
                if (!string.IsNullOrEmpty(Get(13))) company.Add(new XElement(ns + "EconomicActivityType1", Get(13)));

                var idNumsC = new XElement(ns + "IdentificationNumbers");
                if (!string.IsNullOrEmpty(Get(10))) idNumsC.Add(new XElement(ns + "BusinessRegistrationNumber", Get(10)));
                var brd = Get(14);
                if (!string.IsNullOrEmpty(brd) && DateTime.TryParse(brd, out var brdDt)) idNumsC.Add(new XElement(ns + "BusinessRegistrationDate", brdDt.ToString("yyyy-MM-dd")));
                if (idNumsC.HasElements) company.Add(idNumsC);

                var mailingC = new XElement(ns + "MailingAddress");
                var cityC = Get(26) ?? postal ?? string.Empty;
                if (!string.IsNullOrEmpty(cityC)) mailingC.Add(new XElement(ns + "City", cityC));
                mailingC.Add(new XElement(ns + "Country", "LK"));
                mailingC.Add(new XElement(ns + "AddressLine", addressLine));
                company.Add(mailingC);

                var permC = new XElement(ns + "PermanentAddress");
                var cityP = Get(26) ?? postal ?? string.Empty;
                if (!string.IsNullOrEmpty(cityP)) permC.Add(new XElement(ns + "City", cityP));
                permC.Add(new XElement(ns + "Country", "LK"));
                permC.Add(new XElement(ns + "AddressLine", addressLine));
                company.Add(permC);

                company.Add(new XElement(ns + "Contacts"));
                bounced.Add(company);

                // SubjectRole should use company code
                customerCode = compCode;
            }
            else
            {
                var individual = new XElement(ns + "Individual");
                if (!string.IsNullOrEmpty(customerCode) && !customerCode.Contains("-")) customerCode = customerCode + "-1";
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
                var brd2 = Get(14);
                if (!string.IsNullOrEmpty(brd2) && DateTime.TryParse(brd2, out var brdDt2)) idNums.Add(new XElement(ns + "BusinessRegistrationDate", brdDt2.ToString("yyyy-MM-dd")));
                if (idNums.HasElements) individual.Add(idNums);

                var mailing = new XElement(ns + "MailingAddress");
                if (!string.IsNullOrEmpty(Get(26))) mailing.Add(new XElement(ns + "City", Get(26)));
                if (!string.IsNullOrEmpty(postal)) mailing.Add(new XElement(ns + "PostalCode", postal));
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
                if (!string.IsNullOrEmpty(postal)) permanent.Add(new XElement(ns + "PostalCode", postal));
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
            }

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
