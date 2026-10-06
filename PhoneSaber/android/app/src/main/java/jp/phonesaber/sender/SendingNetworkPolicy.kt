package jp.phonesaber.sender

object SendingNetworkPolicy {
    fun canSend(debugBuild: Boolean, developerOverride: Boolean, hasNetwork: Boolean,
                isWifi: Boolean, manualDestination: Boolean): Boolean =
        hasNetwork && (isWifi || (debugBuild && developerOverride && manualDestination))
}
