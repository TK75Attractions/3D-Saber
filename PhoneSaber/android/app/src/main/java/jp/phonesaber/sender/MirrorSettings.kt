package jp.phonesaber.sender

data class MirrorSettings(val mirrorX: Boolean = false, val mirrorY: Boolean = false) {
    fun save(writeBoolean: (String, Boolean) -> Unit) {
        writeBoolean(X_KEY, mirrorX)
        writeBoolean(Y_KEY, mirrorY)
    }

    companion object {
        private const val X_KEY = "mirrorX"
        private const val Y_KEY = "mirrorY"

        fun load(readBoolean: (String, Boolean) -> Boolean) = MirrorSettings(
            readBoolean(X_KEY, false), readBoolean(Y_KEY, false))
    }
}
