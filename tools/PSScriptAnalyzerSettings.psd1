@{
    # Start with actionable security/correctness rules, not a repository-wide style rewrite.
    IncludeRules = @(
        'PSAvoidUsingInvokeExpression'
        'PSAvoidUsingPlainTextForPassword'
        'PSAvoidUsingConvertToSecureStringWithPlainText'
        'PSAvoidUsingUsernameAndPasswordParams'
        'PSAvoidUsingAllowUnencryptedAuthentication'
        'PSAvoidUsingBrokenHashAlgorithms'
        'PSAvoidUsingComputerNameHardcoded'
        'PSMisleadingBacktick'
        'PSReservedParams'
        'PSReservedCmdletChar'
        'PSAvoidInvokingEmptyMembers'
        'PSUseApprovedVerbs'
    )
}
