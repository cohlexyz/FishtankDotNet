using System.ComponentModel.DataAnnotations.Schema;

namespace KfChatDotNetBot.Models.DbModels;

public class KasinoShopProfileDbModel
{
    /// <summary>
    /// ID for the database row
    /// </summary>
    public int Id { get; set; }
    /// <summary>
    /// Shop profiles belong to a user, not their gambler ID
    /// they persist even if the user abandons their profile
    /// </summary>
    public required UserDbModel User { get; set; }
    /// <summary>
    /// Assets held by this profile
    /// </summary>
    public required List<KasinoShopProfileAssetDbModel> Assets { get; set; }
    /// <summary>
    /// Loans taken out by this profile
    /// </summary>
    [InverseProperty(nameof(KasinoShopProfileLoanDbModel.Borrower))]
    public required List<KasinoShopProfileLoanDbModel> LoansTaken { get; set; }
    /// <summary>
    ///  Loans owed to this profile
    /// </summary>
    [InverseProperty(nameof(KasinoShopProfileLoanDbModel.Lender))]
    public required List<KasinoShopProfileLoanDbModel> LoansOwed { get; set; }
    /// <summary>
    /// State of the profile
    /// </summary>
    public required KasinoShopProfileStateFlags State { get; set; } = KasinoShopProfileStateFlags.None;
    /// <summary>
    /// JSON object containing data related to the above states
    /// </summary>
    public required KasinoShopProfileStateDataModel StateData { get; set; }
    /// <summary>
    /// Profile balance in the "Krypto" currency
    /// </summary>
    public required decimal KryptoBalance { get; set; }
}

// Note this is serialized to JSON by Entity Framework so you can go wild shoving random bullshit in here
public class KasinoShopProfileStateDataModel
{
    /// <summary>
    /// Profile credit score for determining creditworthiness etc.
    /// </summary>
    // Actually considered making this uint but I like the idea of negative credit
    public required int KreditScore { get; set; }
    /// <summary>
    /// Amount this user has wagered towards their sponsor requirement
    /// </summary>
    public decimal? SponsorWagerAmount { get; set; } = null;
    /// <summary>
    /// The sponsor's wager requirement
    /// </summary>
    public decimal? SponsorWagerRequirement { get; set; } = null;
    /// <summary>
    /// Modifier that alters the house edge for your gambler entity
    /// </summary>
    public required decimal HouseEdgeModifier { get; set; } = 1;
    /// <summary>
    /// How much crack you've smoked?
    /// </summary>
    public required int CrackCounter { get; set; } = 0;
    /// <summary>
    /// How many floor nugs you got embedded in the carpet
    /// </summary>
    public required int FloorNugs { get; set; } = 0;
    /// <summary>
    /// Time when your weed buff ends
    /// </summary>
    public required DateTimeOffset WeedBuffEnds { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>
    /// Time when your crack buff ends
    /// </summary>
    public required DateTimeOffset CrackBuffEnds { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>
    /// Dodgy stat tracking
    /// </summary>
    public required KasinoShopStatTrackerModel StatTracker { get; set; } = new();
}

public class KasinoShopStatTrackerModel
{
    public decimal TotalDeposited { get; set; } = 0;
    public decimal TotalWithdrawn { get; set; } = 0;
    public decimal TotalLossback { get; set; } = 0;
    /// <summary>
    /// Track wager statistics by game
    /// </summary>
    public Dictionary<WagerGame, decimal> StatTracker { get; set; } = new();
}

public class KasinoShopProfileLoanDbModel
{
    /// <summary>
    /// ID for the database row
    /// </summary>
    public int Id { get; set; }
    /// <summary>
    /// Profile of the user who owns this loan
    /// </summary>
    public required KasinoShopProfileDbModel Borrower { get; set; }
    /// <summary>
    /// Profile of the user to whom this loan is owed/payable to
    /// </summary>
    public required KasinoShopProfileDbModel Lender { get; set; }
    /// <summary>
    /// Amount loaned
    /// </summary>
    public required decimal Amount { get; set; }
    /// <summary>
    /// Amount to be paid out to the loaner
    /// </summary>
    public required decimal PayoutAmount { get; set; }
    /// <summary>
    /// Date and time loan entry was created
    /// </summary>
    public required DateTimeOffset Created { get; set; }
    /// <summary>
    /// State of this loan
    /// </summary>
    public required LoanState State { get; set; }
}

public class KasinoShopProfileAssetDbModel
{
    /// <summary>
    /// ID for the database row
    /// </summary>
    public int Id { get; set; }
    /// <summary>
    /// Profile of the user who owns this asset
    /// </summary>
    public required KasinoShopProfileDbModel Profile { get; set; }
    /// <summary>
    /// Value of the item at the time of acquisition in Krypto
    /// </summary>
    public required decimal OriginalValue { get; set; }
    /// <summary>
    /// What the value of the item is right now
    /// </summary>
    public required decimal CurrentValue { get; set; }
    /// <summary>
    /// Asset name
    /// </summary>
    public required string Name { get; set; }
    /// <summary>
    /// Asset type
    /// </summary>
    public required AssetType AssetType { get; set; }
    /// <summary>
    /// Date and time the asset was acquired
    /// </summary>
    public required DateTimeOffset Acquired { get; set; }
    /// <summary>
    /// History of value changes (e.g. interest events)
    /// </summary>
    public required List<KasinoShopProfileAssetValueChangeDbModel> ValueChangeReports { get; set; }
    /// <summary>
    /// Use this to store enum values for assets that have a subtype (e.g. Car Type)
    /// but were otherwise not special enough to have their own table (e.g. Car)
    /// </summary>
    public int? AssetSubType { get; set; } = null;
    /// <summary>
    /// Serialized JSON for extra information where the schema can't accommodate for you
    /// </summary>
    public string? Extra { get; set; } = null;
}

public class KasinoShopProfileAssetInvestmentDbModel
{
    /// <summary>
    /// ID for the database row
    /// </summary>
    public int Id { get; set; }
    /// <summary>
    ///  Related asset for this investment
    /// </summary>
    public required KasinoShopProfileAssetDbModel Asset { get; set; }
    /// <summary>
    /// What type of investment it is
    /// </summary>
    public required InvestmentType InvestmentType { get; set; }
    /// <summary>
    /// Last time interest was calculated
    /// </summary>
    public required DateTimeOffset LastInterestCalculation { get; set; }
    /// <summary>
    /// Low point for interest calculations
    /// </summary>
    public required float InterestRangeMin { get; set; }
    /// <summary>
    /// High point for interest calculations
    /// </summary>
    public required float InterestRangeMax { get; set; }
    /// <summary>
    /// Use this to store enum values for investments that have a subtype (e.g. Shoe Brand)
    /// but were otherwise not special enough to have their own table (e.g. Shoe)
    /// </summary>
    public int? InvestmentSubType { get; set; } = null;
    /// <summary>
    /// Serialized JSON for extra information where the schema can't accommodate for you
    /// </summary>
    public string? Extra { get; set; } = null;
}

public class KasinoShopProfileAssetValueChangeDbModel
{
    /// <summary>
    /// ID for the database row
    /// </summary>
    public int Id { get; set; }
    /// <summary>
    /// Related asset
    /// </summary>
    public required KasinoShopProfileAssetDbModel Asset { get; set; }
    /// <summary>
    /// Effect of the change
    /// </summary>
    public required decimal ValueChangeEffect { get; set; }
    /// <summary>
    /// Change percent as a decimal fraction?
    /// </summary>
    public required decimal ValueChangePercent { get; set; }
    /// <summary>
    /// Descriptive text for the value change (like the source of it)
    /// </summary>
    public required string Description { get; set; }
}

[Flags]
public enum KasinoShopProfileStateFlags : ulong
{
    None,
    IsSponsored,
    IsWeeded,
    IsCracked,
    IsInWithdrawal,
    IsLoanable
}

[Flags]
public enum KasinoShopProfileAssetState : ulong
{
    None,
    /// <summary>
    /// Only applicable to smashable objects (e.g. PC peripherals)
    /// </summary>
    IsSmashed
}

public enum AssetType
{
    Investment,
    Smashable,
    Car,
    Random
}

public enum InvestmentType
{
    Shoes,
    Stake,
    Gold,
    Silver,
    Skin,
    House,
    Random
}

public enum LoanState
{
    /// <summary>
    /// Loan not fully paid but borrower still in good standing
    /// </summary>
    Active,
    /// <summary>
    /// Past due but not yet a serious violation of terms
    /// </summary>
    Delinquent,
    /// <summary>
    /// Loan terms violated, time to collect
    /// </summary>
    Default,
    /// <summary>
    /// Loan settled by agreement to amended terms (e.g. paid off less than the full amount)
    /// </summary>
    Settled,
    /// <summary>
    /// Loan fully repaid for the total amount and closed out
    /// </summary>
    Repaid,
    /// <summary>
    /// Written off debt due to being unable to collect
    /// </summary>
    Uncollectible,
    /// <summary>
    /// Administrative state for loans canceled due to serious malfeasance
    /// </summary>
    Canceled
}