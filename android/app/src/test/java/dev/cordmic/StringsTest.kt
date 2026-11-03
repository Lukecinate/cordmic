package dev.cordmic

import java.io.File
import javax.xml.parsers.DocumentBuilderFactory
import org.junit.Assert.assertEquals
import org.junit.Test

/** English (res/values) and Indonesian (res/values-in) must define the same translatable strings. */
class StringsTest {
    private fun keys(dir: String): Map<String, Int> {
        val f = File("src/main/res/$dir/strings.xml")
        val doc = DocumentBuilderFactory.newInstance().newDocumentBuilder().parse(f)
        val nodes = doc.getElementsByTagName("string")
        return (0 until nodes.length).map { nodes.item(it) as org.w3c.dom.Element }
            .filter { it.getAttribute("translatable") != "false" }
            .associate { it.getAttribute("name") to Regex("%\\d\\$").findAll(it.textContent).count() }
    }

    @Test fun englishAndIndonesianHaveSameKeysAndPlaceholders() {
        val en = keys("values")
        val id = keys("values-in")
        assertEquals(en.keys.sorted(), id.keys.sorted())
        for (k in en.keys) assertEquals("placeholders in $k", en[k], id[k])
    }
}
